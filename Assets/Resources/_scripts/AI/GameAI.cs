﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum AICommunicationState {
    NotInCommuncation,
    InCommunication
}

public enum AIAlignmentState {
    Neutral,
    Friendly,
    VeryFriendly,
    Hostile,
    VeryHostile
}

public class GameAI : MonoBehaviour {
    public Player player;
    public Maze maze;
    private CommunicationChannel currentCommChannel;

    private TextCommunicationChannel textCommChannel;
    private OneWayTextCommunication oneWayCommChannel;
    private RoomExitPathCommChannel roomExitCommChannel;
    private OneWayTimedCommChannel oneWayTimedComm;
    private StillnessTimedCommChannel stillnessTimedComm;

    private ObjectMover objectMover;

    private IntVector2 playerCurrentCoords;

    private AICommunicationState aiCommState;
    private AIAlignmentState aiAlignmentState;

    private PlayerResponse playerResponse;

    private bool openingDone = false;
    private bool firstInterchangeDone = false;

    private AiPlayerInterchange currentInterchange;

    private Dictionary<AIAlignmentState, List<Action>> perStateRequestActionList;
    private Dictionary<AIAlignmentState, List<Action>> perStateReactionList;

    private System.Random rng;

    private int numberOfInfractions = 0;

    private bool reactToPlayer = false;

    public bool gameOver = false;

    private Dictionary<IntVector2, bool> roomsRequestedIn;

    private void Start() {

        //init communcation channels 
        textCommChannel = CommunicationChannelFactory.Make2WayTextChannel() as TextCommunicationChannel;
        oneWayCommChannel = CommunicationChannelFactory.MakeOneWayTextChannel() as OneWayTextCommunication;
        roomExitCommChannel = CommunicationChannelFactory.MakeRoomExitPathChannel() as RoomExitPathCommChannel;
        oneWayTimedComm = CommunicationChannelFactory.MakeOneWayTimedChannel() as OneWayTimedCommChannel;
        stillnessTimedComm = CommunicationChannelFactory.MakeTimedStillnessChannel() as StillnessTimedCommChannel;

        //init object mover
        objectMover = ObjectMover.CreateObjectMover();

        playerCurrentCoords = player.MazeCellCoords;

        //start out not in communcation
        aiCommState = AICommunicationState.NotInCommuncation;
        aiAlignmentState = AIAlignmentState.Neutral;

        //initialize list of possible actions (for each state)
        InitializeActionLists();

        roomsRequestedIn = new Dictionary<IntVector2, bool>();

        rng = new System.Random();
    }

    //code to intialize the action lists for each state based on the AI's player-affecting methods.
    //consider refactoring this into a superclass that this derives from.
    #region
    //helper function to convert a list of methodInfo objects into Actions
    private List<Action> GetActionListFromMethodInfos(IEnumerable<MethodInfo> methodInfos) {
        return new List<Action> (methodInfos.Select(m => (Action)Delegate.CreateDelegate(typeof(Action), this, m, false)));
    }

    //helper function to filter a list of MethodInfo objects by name
    private IEnumerable<MethodInfo> FilterMethodInfosByNameStart(IEnumerable<MethodInfo> methodInfos, string nameStart) {
        return methodInfos.Where(m => m.Name.StartsWith(nameStart));
    }

    //helper function to combine the above 2 and add any extra needed actions to the list (only null action right now)
    private List<Action> GetActionsByNameStart(IEnumerable<MethodInfo> methodInfos, string nameStart, 
                                                bool addNullAction = true) {
        var actions = GetActionListFromMethodInfos(FilterMethodInfosByNameStart(methodInfos, nameStart));
        if (addNullAction) {
            actions.Add(NullAction);
        }
        return actions;
    }

    //helper function to turn all of the requests in a given state's GameLines directory into
    //Actions that execute the interaction as a generic text interchange.
    private IEnumerable<Action> CreateTextRequestActionList(string stateName, bool randomize = true) {
        string requestPath = string.Format("requests/text_requests/{0}/", stateName);
        IEnumerable<GenericTextInterchange> interchanges = GameLinesTextGetter.ParseAllTextInterchangesInDir(requestPath);

        //randomize order
        if (randomize) {
            var rng = new System.Random();
            interchanges = interchanges.OrderBy(i => rng.Next());
        }

        //the only way to get the desired behavior is via an enumerator.
        //Each function will get the next item from the enumerator, and there
        //will be exactly as many functions as items
        IEnumerator<GenericTextInterchange> interchangesEnumerator = interchanges.GetEnumerator();
        for (int i = 0; i < interchanges.Count(); i++) {
            yield return () => {
                if (interchangesEnumerator.MoveNext()) {
                    if (interchangesEnumerator.Current == null) {
                        Debug.Log("next interchange was null");
                    }
                    ExecTextInterchange(interchangesEnumerator.Current);
                }
            };
            
        }
    }

    //helper function to turn all of the reaction text in a given state's GameLines directory
    //into actions that send the message on the one way channel. Some code duplication from above,
    //but combining these two functions would probably make them less comprehensible
    private IEnumerable<Action> CreateTextReactionActionList(string stateName, bool randomize = true) {
        string responsePath = string.Format("reactions/{0}", stateName);
        IEnumerable<string> reactions = GameLinesTextGetter.GetAllTextInDir(responsePath);

        if (randomize) {
            var rng = new System.Random();
            reactions = reactions.OrderBy(i => rng.Next());
        }

        IEnumerator<string> reactionEnumerator = reactions.GetEnumerator();
        for (int i = 0; i < reactions.Count(); i++) {
            yield return () => {
                if (reactionEnumerator.MoveNext()) {
                    SendMessageToPlayer(reactionEnumerator.Current, oneWayCommChannel);
                }
            };
        }
    }

    /// <summary>
    /// Initialize the action lists for each alignment state. This gets every 0-param, void-returning method in the object
    /// that starts with the state's name (plus "_") and puts it in a list for that state.
    /// Neutral actions are added to every state.
    /// Note that ALL methods designated as state actions with a State + _ name must be void return type and take no arguments for this to work.
    /// </summary>
    private void InitializeActionLists() {
        perStateRequestActionList = new Dictionary<AIAlignmentState, List<Action>>();
        perStateReactionList = new Dictionary<AIAlignmentState, List<Action>>();

        //get the methods of this type
        var aiMethods = typeof(GameAI).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        
        foreach (AIAlignmentState state in Enum.GetValues(typeof(AIAlignmentState))) {
            string stateName = state.ToString();
            bool addNullAction = !stateName.StartsWith("Very");

            //add the request methods twice to the list for request methods for each state
            perStateRequestActionList[state] = GetActionsByNameStart(aiMethods, stateName + "_Request_", addNullAction);

            for (int i = 0; i < 2; i++) {
                perStateRequestActionList[state].AddRange(GetActionsByNameStart(aiMethods, stateName + "_Request_", addNullAction));
            }

            //add the generic text requests found in the GameLines folder for this state as actions:
            perStateRequestActionList[state].AddRange(CreateTextRequestActionList(stateName));            
            

            //add the reaction methods twice to the list for reaction methods for each state
            perStateReactionList[state] = GetActionsByNameStart(aiMethods, stateName + "_Reaction_", addNullAction);

            for (int i = 0; i < 2; i++) {
                perStateReactionList[state].AddRange(GetActionsByNameStart(aiMethods, stateName + "_Reaction_", addNullAction));
            } 
            

            //add reactions found in the GameLines folder for this state:
            bool randomize = (!stateName.StartsWith("Very"));
            perStateReactionList[state].AddRange(CreateTextReactionActionList(stateName, randomize));
        }
    }
    #endregion


    //Main business logic. Contains update function (called every frame), code to initiate actions/communications
    //with player, handle responses, and change alignment states
    #region
    private float DistanceBetweenPlayerAndRoom(IntVector2 roomCoords) {
        return Vector3.Distance(maze.GetCellLocalPosition(roomCoords.x, roomCoords.z),
                                player.transform.localPosition);
    }

    private void ExecuteRandomAction(List<Action> possibleActions) {
        if (possibleActions.Count == 0) {
            NullAction();
            return;
        }

        int randIdx = rng.Next(0, possibleActions.Count);
        Action randAction = possibleActions[randIdx];
        randAction();

        //testing this feature: remove an action after its done so it cant happen again.
        possibleActions.RemoveAt(randIdx);
    }

    private void Update() {
        //if in communcation, check for response, call handler on response if there
        if (aiCommState == AICommunicationState.InCommunication) {
            if (currentCommChannel != null && currentCommChannel.IsResponseReceived()) {
                playerResponse = currentCommChannel.GetResponse();

                //end communcation and reset state
                currentCommChannel.EndCommuncation();
                currentCommChannel = null;

                aiCommState = AICommunicationState.NotInCommuncation;

                //handle whatever the response was
                HandleResponse(playerResponse);
            }
        }
        else if (!openingDone) {
            Debug.LogError("about to close doors in cell opening");
            maze.CloseDoorsInCell(playerCurrentCoords);
            SendMessageToPlayer(GameLinesTextGetter.OpeningMonologue(), oneWayCommChannel);
        }
        else if (playerCurrentCoords != player.MazeCellCoords && 
                 DistanceBetweenPlayerAndRoom(player.MazeCellCoords) < 0.4) {

            //neutral ending. ends on reaching the ladder
            if (player.MazeCellCoords == maze.exitCoords && !aiAlignmentState.ToString().StartsWith("Very")) {
                FlyoverMonologueEnding();
            }
            //very friendly ending. end condition on the last room
            else if (aiAlignmentState == AIAlignmentState.VeryFriendly 
                      && player.MazeCellCoords.z == (maze.size.z - 1)) {
                maze.TurnAllLightsRed();

                Debug.LogError("about to close doors in cell friendlyending");
                maze.CloseDoorsInCell(player.MazeCellCoords);
                gameOver = true;
            }
            //the standard case. do a reaction or request
            else {
                //for the single hallway ending. close doors behind you.
                if (aiAlignmentState == AIAlignmentState.VeryFriendly) {
                    Debug.LogError("about to close doors in cell friendlyending close behind");
                    maze.CloseDoorsInCell(playerCurrentCoords);
                    
                }

                playerCurrentCoords = player.MazeCellCoords;
                if (!firstInterchangeDone) {
                    Neutral_Request_AskPlayerToTouchCorners();
                    firstInterchangeDone = true;
                }
                else {
                    if (reactToPlayer) {
                        ExecuteRandomAction(perStateReactionList[aiAlignmentState]);

                        //react to player next time only if VeryHostile, VeryFriendly and there are reactions left
                        reactToPlayer = (aiAlignmentState.ToString().StartsWith("Very")) &&
                            perStateReactionList[aiAlignmentState].Count > 0;
                    }
                    else {
                        if (!roomsRequestedIn.ContainsKey(playerCurrentCoords)) {
                            ExecuteRandomAction(perStateRequestActionList[aiAlignmentState]);
                            roomsRequestedIn[playerCurrentCoords] = true;
                        }
                        
                        //on occasion, prompt a reaction from the AI on the next room
                        reactToPlayer = (UnityEngine.Random.Range(0, 1f) < 0.75f);
                    }
                }
            }            
        }
    }

    /// <summary>
    /// Handle a response from a communcation channel (to be expanded)
    /// </summary>
    /// <param name="response"></param>
    private void HandleResponse(PlayerResponse response) {
        if (!openingDone) {
            openingDone = true;
            maze.OpenDoorsInCell(playerCurrentCoords);
        }


        //if there was no interchange, no response was expected, so do nothing
        if (currentInterchange == null) {
            return;
        }
        //otherwise, check response, change state, and respond as needed
        else {
            //reopen doors if they were closed
            maze.OpenDoorsInCell(playerCurrentCoords);

            ThreeState wasResponseCorrect = currentInterchange.CheckIfCorrectResponse(response);
            Debug.Log(wasResponseCorrect.ToBool());
            Debug.Log(response.responseStr);
            string responseText = currentInterchange.GetResponseToPlayerText(wasResponseCorrect.ToBool());
            SendMessageToPlayer(responseText, oneWayCommChannel);

            //ending condition for veryhostile
            if (aiAlignmentState == AIAlignmentState.VeryHostile) {
                
                if (wasResponseCorrect == ThreeState.True) {
                    AscendPlayer();
                    gameOver = true;
                }
                else {
                    ReduceMazeToOneRoom();

                }
            }
            //otherwise do state transition if need be
            else if (wasResponseCorrect != ThreeState.Neutral && 
                !aiAlignmentState.ToString().StartsWith("Very")) {
                StateTransition(wasResponseCorrect.ToBool());
            }
            
            //reset current interchange to get caught by above conditional.
            currentInterchange = null;
        }
    }

    private void StateTransition(bool responseWasPositive) {
        numberOfInfractions += (responseWasPositive ? -1 : 1);
        if (numberOfInfractions <= 2 && numberOfInfractions >= -2) {
            aiAlignmentState = AIAlignmentState.Neutral;
        }
        else if (numberOfInfractions < -2) {
            if (numberOfInfractions <= -6) {
                aiAlignmentState = AIAlignmentState.VeryFriendly;
                SingleHallwayEnding();
            }
            else {
                aiAlignmentState = AIAlignmentState.Friendly;
            }
            
        }
        else if (numberOfInfractions > 2) {
            if (numberOfInfractions >= 6) {
                aiAlignmentState = AIAlignmentState.VeryHostile;
                CircleMazeEnding();
            }
            else {
                aiAlignmentState = AIAlignmentState.Hostile;
            }
        }
        
    }
    #endregion

    // Ai player communcation helper functions. Functions that handle communicating with player through the AI's
    // communcation channels.
    #region
    private PlayerPath GetPlayerCornerPath() {
        Vector3 localRoomPos = maze.GetCellLocalPosition(playerCurrentCoords.x, playerCurrentCoords.z);

        var pointList = new List<Vector3> {
            localRoomPos + new Vector3(0.5f, 0, 0.5f),
            localRoomPos + new Vector3(-0.5f, 0, 0.5f),
            localRoomPos + new Vector3(0.5f, 0, -0.5f),
            localRoomPos + new Vector3(-0.5f, 0, -0.5f),
        };

        return new PlayerPath(pointList, initWithListOrder: false);
    }

    private void RequestPlayerToFollowPath(PathInterchange pathInterchange, PathCommuncationChannel channel) {
        currentInterchange = pathInterchange;
        channel.SetPathForPlayer(pathInterchange.expectedResponse.playerPath);
        SendMessageToPlayer(pathInterchange.GetQuestionText(), channel);

    }

    /// <summary>
    /// sends the given string message to the player via the given channel. 
    /// Also sets current channel to be given channel.
    /// </summary>
    /// <param name="message"></
    /// <param name="channel"></param>
    private void SendMessageToPlayer(string message, CommunicationChannel channel) {
        if (channel == null) {
            throw new Exception("channel was null");
        }
        aiCommState = AICommunicationState.InCommunication;
        currentCommChannel = channel;
        channel.StartCommunicationWithPlayer(player, this, message);
    }

    /// <summary>
    /// Given a generic text interchange object, executes the interchange on the 2 way commchannel
    /// and closes doors on the player
    /// </summary>
    /// <param name="interchange"></param>
    private void ExecTextInterchange(GenericTextInterchange interchange) {
        if (interchange == null) {
            Debug.LogError("interchange was null in ExecTextInterchange");
            return;
        }

        Debug.LogError("about to close doors in cell exectextinterchange");
        maze.CloseDoorsInCell(playerCurrentCoords);

        currentInterchange = interchange;

        SendMessageToPlayer(currentInterchange.GetQuestionText(), textCommChannel);
    }
    #endregion


    /// <summary>
    /// Below this point in the code are the AI's state specific high-level actions. The ai chooses between these
    /// actions randomly depending on its current alignment state.
    /// </summary>

    private void NullAction() {
        return;
    }

    // Neutral AI actions
    #region
    private void Neutral_Request_AskPlayerToTouchCorners() {
        var pathToFollow = GetPlayerCornerPath();
        var cornerInterchange = new TouchCornersInterchange(aiAlignmentState,
                                                            new PlayerResponse(pathToFollow, false), 
                                                            !firstInterchangeDone);
        RequestPlayerToFollowPath(cornerInterchange, roomExitCommChannel);
    }

    private void Neutral_Request_AskPlayerToStandStill() {
        Debug.LogError("about to close doors in cell standstill");
        maze.CloseDoorsInCell(playerCurrentCoords);
        currentInterchange = new StayStillInterchange(aiAlignmentState);
        SendMessageToPlayer(currentInterchange.GetQuestionText(), stillnessTimedComm);
    }


    #endregion

    //Hostile AI actions
    #region

    private void Hostile_Request_LockPlayerInRoom() {
        Debug.LogError("about to close doors in cell LockPlayerInRoom");
        maze.CloseDoorsInCell(playerCurrentCoords);
        var interchange = new LockPlayerInRoomInterchange(aiAlignmentState);
        interchange.timeLocked = 5.0f;
        currentInterchange = interchange;

        oneWayTimedComm.SetTimeToWait(5.0f);

        SendMessageToPlayer(currentInterchange.GetQuestionText(), oneWayTimedComm);
    }

    private void Hostile_Request_NastyLimerickCompletion() {

    }

    private void Hostile_Reaction_TurnLightsRed() {
        maze.RemoveAllSignPosts();
        maze.TurnAllLightsRed();
    }

    private void Hostile_Reaction_SpinTheMaze() {
        oneWayTimedComm.SetTimeToWait(5.0f);
        SendMessageToPlayer(GameLinesTextGetter.SpinMazeText, oneWayTimedComm);

        Action<GameObject> onFinish = (obj => obj.GetComponent<Player>().UnfreezePlayer());

        player.FreezePlayer();
        bool success = objectMover.SpinObject(player.gameObject, 4750f, 300.0f, onFinish);

        if (!success) {
            Debug.LogError("ObjectMover failed to spin the Player, it was already busy.");
        }
    }

    private void Hostile_Reaction_LengthenHallways() {
        SendMessageToPlayer(GameLinesTextGetter.LengthenHallwaysText, oneWayCommChannel);
        maze.ChangeHallwayLength(maze.RoomSeparationDistance + 3.0f, player);
    }

    //TODO: Make sure this is working
    private void Hostile_Reaction_LengthenPathToExit() {
        MazeDirection? longcutDir = maze.LengthenPathToExitIfPossible(playerCurrentCoords);
        bool longcutPossible = longcutDir != null;

        oneWayTimedComm.SetTimeToWait(5.0f);
        SendMessageToPlayer(GameLinesTextGetter.LongcutText(longcutPossible), oneWayTimedComm);

        if (longcutPossible) {
            maze.AddSignpostToCell(playerCurrentCoords, longcutDir.GetValueOrDefault(), player.transform.localPosition);
        }
    }

    private void Hostile_Reaction_TheBeastIsNear() {
        Debug.LogError("about to close doors in cell beastisnear");
        maze.CloseDoorsInCell(playerCurrentCoords, doItInstantly: true);

        SendMessageToPlayer(GameLinesTextGetter.BeastIsNearText, oneWayCommChannel);

        //realign the object to normal rotation after shaking is done
        Action<GameObject> onFinish =
            (obj) => {
                    obj.transform.localRotation = Quaternion.LookRotation(obj.transform.forward);
                    obj.GetComponentInParent<Maze>().OpenDoorsInCell(playerCurrentCoords);
                };

        objectMover.ShakeObject(maze.GetCell(playerCurrentCoords).gameObject, 
                                new Vector3(0, 0, 1f), 30, 200f, 10f, onFinish);
    }

    private void Hostile_Reaction_GiveFalseHint() {
        SendMessageToPlayer(GameLinesTextGetter.FalseHintText, oneWayCommChannel);

        maze.AddSignpostToCell(playerCurrentCoords, MazeDirections.RandDirection, player.transform.localPosition);
    }

    private void Hostile_Reaction_DestroyBreadcrumbs() {
        SendMessageToPlayer(GameLinesTextGetter.DestroyYourBreadcrumbsText, oneWayCommChannel);

        player.DestroyDroppedBreadcrumbs();
    }

    private void Hostile_Reaction_ReduceBreadcrumbs() {
        SendMessageToPlayer(GameLinesTextGetter.ReduceBreadCrumbsText, oneWayCommChannel);

        if (player.maxBreadcrumbs > 5) {
            player.maxBreadcrumbs -= 5;
        }
        else {
            player.maxBreadcrumbs = 0;
        }
    }

    #endregion

    //Friendly AI actions
    #region

    private void Friendly_Reaction_GiveHint() {
        List<IntVector2> pathToExit = maze.GetPathToExit(playerCurrentCoords);

        SendMessageToPlayer("Have a hint. You've earned it.", oneWayCommChannel);
        MazeDirection wayToMove = (pathToExit[1] - playerCurrentCoords).ToDirection();
        maze.AddSignpostToCell(playerCurrentCoords, wayToMove, player.transform.localPosition);
    }

    private void Friendly_Reaction_AddGridLocationsToWalls() {
        SendMessageToPlayer("The coordinates of each cell may help you navigate, friend.", oneWayCommChannel);
        maze.AddCoordsToAllCells();
    }

    private void Friendly_Reaction_CreateShortcut() {
        MazeDirection? shortcutDir = maze.CreateShortcutIfPossible(playerCurrentCoords);
        bool shortcutPossible = shortcutDir != null;

        oneWayTimedComm.SetTimeToWait(5.0f);
        SendMessageToPlayer(GameLinesTextGetter.ShortcutText(shortcutPossible), oneWayTimedComm);

        if (shortcutPossible) {
            maze.AddSignpostToCell(playerCurrentCoords, shortcutDir.GetValueOrDefault(), player.transform.localPosition);
        }
    }

    private void Friendly_Reaction_GiveMoreBreadCrumbs() {
        SendMessageToPlayer(GameLinesTextGetter.GiveMoreBreadcrumbsText, oneWayCommChannel);

        player.maxBreadcrumbs += 10;
    }

    #endregion


    //This region contains code for ending the game.
    //There are currently 3 types of endings the AI can initiate.
    #region

    //start off the end of the game. ending changes depending on ai state.
    private void FlyoverMonologueEnding() {
        Debug.LogError("about to close doors in cell monologueending");
        maze.CloseDoorsInCell(playerCurrentCoords);
        player.PermanentlyFreezePlayer();
        SendMessageToPlayer(GameLinesTextGetter.GetEndingMonologue(AIAlignmentState.Neutral), oneWayCommChannel);

        ObjectMover objMoverTwo = ObjectMover.CreateObjectMover();

        objectMover.MoveObjectStraightLine(player.gameObject, new Vector3(0, 2.0f, 0), 1f);

        Action<GameObject> setGameOverFlag = (obj => gameOver = true);
        objMoverTwo.SpinObject(player.gameObject, 600f, 30f, setGameOverFlag);

        maze.StartRandomizingMaze(2.0f);
    }

    //resize the maze and place the player in it.
    //optional parameter mazeGen allows caller to define how the maze will be generated after being destroyed
    //(defaults to maze.Generate())
    private void ResizeMaze(IntVector2 newSize, IntVector2 newPlayerCoords, Action<Maze> mazeGen = null) {
        
        player.FreezePlayer();
        maze.DestroyCurrentMaze();

        maze.size = newSize;

        //generate the maze
        if (mazeGen == null) {
            maze.Generate();
        }
        else {
            mazeGen(maze);
        }
        

        playerCurrentCoords = newPlayerCoords;

        Vector3 playerPos = maze.GetCellLocalPosition(newPlayerCoords.x, newPlayerCoords.z);
        playerPos.y += 0.2f;

        player.transform.localPosition = playerPos;

        player.UnfreezePlayer();

        //reset the rooms requested in dict
        roomsRequestedIn = new Dictionary<IntVector2, bool>();
    }

    private void SingleHallwayEnding() {
        ResizeMaze(new IntVector2(1, 10), new IntVector2(0, 0));
        aiAlignmentState = AIAlignmentState.VeryFriendly;
        reactToPlayer = true;
    }

    private void CircleMazeEnding() {
        ResizeMaze(new IntVector2(3, 3), new IntVector2(0, 0), new Action<Maze>(m => m.GenerateCircleMaze()));
        aiAlignmentState = AIAlignmentState.VeryHostile;
        reactToPlayer = true;
    }

    private void AscendPlayer() {
        objectMover.MoveObjectStraightLine(player.gameObject, 
            player.gameObject.transform.localPosition + new Vector3(0, 20f, 0),
            1f);
    }

    private void ReduceMazeToOneRoom() {
        ResizeMaze(new IntVector2(1, 1), new IntVector2(0,0));
        objectMover.SpinObject(player.gameObject, 720f, 50f, new Action<GameObject>(obj => gameOver = true));
    }

    #endregion

    //useful for restarting the game. destroy everything the AI could have active
    public void HaltAllActivityAndSelfDestruct() {
        if (currentCommChannel != null) {
            currentCommChannel.EndCommuncation();
        }

        if (currentInterchange != null) {
            currentInterchange = null;
        }

        Destroy(gameObject);
    }
}