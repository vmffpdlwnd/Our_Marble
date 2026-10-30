using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbyNetwork : Singleton<LobbyNetwork>
{
    protected override bool PersistAcrossScenes => true;
    
    private Queue<LobbyServerMessage> messageQueue = new Queue<LobbyServerMessage>();

    private bool isLobbySceneActive = false;

    protected override void OnAwake()
    {
        Application.runInBackground = true; // 창이 뒤로 가도 멈추지 않게 한다 (PC 두 대로 테스트할 때 필요).
    }
    
    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }
    
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // 씬이 로드될 딱 한 번만 검사해서 플래그를 켭니다.
        isLobbySceneActive = (scene.path == SceneFlow.LobbyScenePath); // 씬 이름이 아니라 경로로 확인한다
    }
    
    /// <summary>
    /// 요청을 받는 쪽. 정하지 않으면 이 PC 안의 로컬 호스트가 받는다.
    /// (Photon을 붙이면 방장은 로컬 호스트가, 참가자는 방장에게 보내는 쪽이 받게 바뀐다)
    /// </summary>
    public IHostRequestReceiver Host { get; set; }

    private void SendLobbyMessage(string message)
    {
        IHostRequestReceiver receiver = Host ?? LocalLobbyHost.Instance;
        receiver.OnRequest(LocalPlayer.Id, message);
    }
    /// <summary>쌓여 있는 서버 메시지를 모두 버린다. 로그아웃하거나 연결이 끊겼을 때, 지난 접속의 메시지가 다음 접속에 처리되지 않게 한다.</summary>
    public void ClearQueue()
    {
        lock (messageQueue) { messageQueue.Clear(); }
    }

    public void ReceiveLobbyMessage(LobbyMessageType parsedType, string json)
    {
        LobbyServerMessage serverMessage = new LobbyServerMessage
        {
            Type = parsedType,
            Json = json
        };

        lock (messageQueue) // 메인 스레드와 네트워크 스레드 충돌 대비 락 (네트워크 스레드가 Enqueue하는 동안에는 메인 스레드의 접근 차단)
        {
            messageQueue.Enqueue(serverMessage);
        }
        
        Debug.Log($"[LobbyNetwork] 메시지 큐 적재: {parsedType} (현재 큐 대기 수: {messageQueue.Count})");
    }
    
    public void TryDequeueMessage()
    {
        LobbyServerMessage serverMessage;
        
        // 현재 씬이 로비이면 다 바로바로 꺼내면 된다.
        // 근데 현재 씬이 로비씬이 아니면? 에러만 꺼내야한다.
        lock (messageQueue)
        {
            if (messageQueue.Count > 0)
            {
                serverMessage = messageQueue.Peek();

                if (!isLobbySceneActive) // 로비씬이 아닌 경우 (게임씬인 경우) 
                {
                    if (serverMessage.Type != LobbyMessageType.LOBBY_ERROR) // 로비에러를 제외한 로비 메시지는 꺼내지 않는다. ROOM_UPDATED가 유실되지 않게 하기 위함.
                        return;
                }
                
                Debug.Log($"[LobbyNetwork] 메시지 디큐. type : {serverMessage.Type}");
                messageQueue.Dequeue(); 
            }
            else
            {
                return;
            }
        }
        
        LobbyMessageType type = serverMessage.Type;
        string json = serverMessage.Json;

        switch (type)
        {
            case LobbyMessageType.ROOM_ENTERED:
            {
                RoomEnteredResponse roomEnteredResponse =
                    JsonConvert.DeserializeObject<RoomEnteredResponse>(json);
                
                OnRoomEntered?.Invoke();
                break;
            }
            case LobbyMessageType.ROOM_LEFT:
            {
                RoomLeftResponse roomLeftResponse =
                    JsonConvert.DeserializeObject<RoomLeftResponse>(json);
                
                OnRoomLeft?.Invoke();
                break;
            }
            case LobbyMessageType.ROOM_LIST:
            {
                RoomListResponse roomListResponse =
                    JsonConvert.DeserializeObject<RoomListResponse>(json);
                
                List<RoomSummary> rooms = roomListResponse.rooms;
                
                OnRoomList?.Invoke(rooms);
                break;
            }
            case LobbyMessageType.ROOM_UPDATED:
            {
                RoomUpdatedBroadcast roomUpdatedBroadcast =
                    JsonConvert.DeserializeObject<RoomUpdatedBroadcast>(json);

                RoomSnapshot roomSnapshot = roomUpdatedBroadcast.room;
                
                OnRoomUpdated?.Invoke(roomSnapshot);
                break;
            }
            case LobbyMessageType.GAME_STARTED:
            {
                GameStartedBroadcast gameStartedBroadcast =
                    JsonConvert.DeserializeObject<GameStartedBroadcast>(json);

                string roomId = gameStartedBroadcast.roomId;
                
                OnGameStarted?.Invoke(roomId);
                break;
            }
            case LobbyMessageType.LOBBY_ERROR:
            {
                LobbyErrorResponse lobbyErrorResponse =
                    JsonConvert.DeserializeObject<LobbyErrorResponse>(json);

                string code = lobbyErrorResponse.code;
                string message = lobbyErrorResponse.message;
                
                Debug.LogError($"에러 메시지 수신. code: {code}, message: {message}");
                
                // 여기서 뭘해야하지..? 떠잇는 로딩 창 닫기?
                OnLobbyError?.Invoke(code, message);
                break;
            }
            case LobbyMessageType.PLAYER_RECORD:
            {
                PlayerRecordResponse playerRecordResponse =
                    JsonConvert.DeserializeObject<PlayerRecordResponse>(json);

                string nickname = playerRecordResponse.nickname;
                int gamesPlayed = playerRecordResponse.gamesPlayed;
                int gamesWon = playerRecordResponse.gamesWon;
                
                OnGameRecord?.Invoke(nickname, gamesPlayed, gamesWon);
                break;
            }
            case LobbyMessageType.CHAT_BROADCAST:
            {
                ChatBroadcast chatBroadcast =
                    JsonConvert.DeserializeObject<ChatBroadcast>(json);

                OnChatBroadcast?.Invoke(chatBroadcast.playerId, chatBroadcast.content);
                break;
            }
            default:
            {
                Debug.LogError($"식별할 수 없는 type입니다.: {type}");
                break;
            }
        }
    }
    
    public event Action<string> OnConnected;
    
    public event Action<string,int,int> OnGameRecord;

    public void InvokeOnConnected(string localNickname)
    {
        OnConnected?.Invoke(localNickname);
    }

    public event Action OnRoomEntered;
    public event Action OnRoomLeft;
    public event Action<List<RoomSummary>> OnRoomList;
    public event Action<RoomSnapshot> OnRoomUpdated;
    public event Action<string> OnGameStarted;
    public event Action<string, string> OnLobbyError;
    public event Action<long, string> OnChatBroadcast; // 보낸 사람 playerId, 내용
   
    public void SendCreateRoomRequest()
    {
        Debug.Log("[Net] C");
        CreateRoomRequest message = new CreateRoomRequest
        {
            type = "CREATE_ROOM"
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
    
    public void SendEnterRoomRequest(string roomId)
    {
        EnterRoomRequest message = new EnterRoomRequest
        {
            type = "ENTER_ROOM",
            roomId = roomId
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
    
    public void SendLeaveRoomRequest()
    {
        LeaveRoomRequest message = new LeaveRoomRequest
        {
            type = "LEAVE_ROOM"
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
    
    public void SendSetReadyRequest(bool ready)
    {
        SetReadyRequest message = new SetReadyRequest
        {
            type = "SET_READY",
            ready = ready
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
    

    public void SendSetPortraitRequest(int portraitId)
    {
        SetPortraitRequest message = new SetPortraitRequest
        {
            type = "SET_PORTRAIT",
            portraitId = portraitId
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }

    public void SendStartGameRequest()
    {
        StartGameRequest message = new StartGameRequest
        {
            type = "START_GAME"
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
    
    public void SendChatRequest(string content)
    {
        ChatSendRequest message = new ChatSendRequest
        {
            type = "CHAT_SEND",
            content = content
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }

    public void SendRoomListRequest()
    {
        RoomListRequest message = new RoomListRequest
        {
            type = "ROOM_LIST_REQUEST"
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
    
    public void SendRoomConfigRequest(GameConfig config, int maxPlayers, int botCount)
    {
        RoomConfigRequest message = new RoomConfigRequest
        {
            type = "ROOM_CONFIG_REQUEST",
            config = config,
            maxPlayers = maxPlayers,
            botCount = botCount
        };

        string json = JsonConvert.SerializeObject(message);
        SendLobbyMessage(json);
    }
}

public class LobbyServerMessage
{
    public LobbyMessageType Type;
    public string Json;
}