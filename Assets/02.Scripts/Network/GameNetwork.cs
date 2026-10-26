using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class GameNetwork : Singleton<GameNetwork>
{
    protected override bool PersistAcrossScenes => false;
    
    private Queue<GameServerMessage> messageQueue = new Queue<GameServerMessage>();
    
    /// <summary>
    /// 요청을 받는 쪽. 게임을 진행하는 호스트가 정해지면 여기에 넣는다.
    /// 아직 없으면 요청을 보내지 않고 경고만 남긴다.
    /// </summary>
    public IHostRequestReceiver Host { get; set; }

    private void SendGameMessage(string message)
    {
        if (Host == null)
        {
            Debug.LogWarning($"[GameNetwork] 요청을 받을 호스트가 아직 없어 보내지 않았습니다: {message}");
            return;
        }

        Host.OnRequest(LocalPlayer.Id, message);
    }

    public void ReceiveGameMessage(GameMessageType parsedType, string json)
    {
        // GameState 동기화에 대한 메시지인 경우 큐를 거치지 않고 즉시 처리
        if(parsedType ==  GameMessageType.GAME_STATE_SYNC)
        {
            HandleGameStateSync(json);
            return;
        }
        
        GameServerMessage serverMessage = new GameServerMessage
        {
            Type = parsedType,
            Json = json
        };

        lock (messageQueue) // 메인 스레드와 네트워크 스레드 충돌 대비 락 (네트워크 스레드가 Enqueue하는 동안에는 메인 스레드의 접근 차단)
        {
            messageQueue.Enqueue(serverMessage);
        }
        
        Debug.Log($"[GameNetwork] 메시지 큐 적재: {parsedType} (현재 큐 대기 수: {messageQueue.Count})");
    }
    
    public void TryDequeueMessage(GameMessageType waitingType)
    {
        GameServerMessage serverMessage;
        
        lock (messageQueue)
        {
            if (messageQueue.Count > 0)
            {
                serverMessage = messageQueue.Dequeue();
            }
            else
            {
                return;
            }
        }
        
        GameMessageType type = serverMessage.Type;
        string json = serverMessage.Json;
        
        // 현재 클라이언트에서 기대하는 type과 큐의 가장 앞에있는 type이 일치하지 않는다면, 클라이언트의 시나리오와 서버의 시나리오가 다르게 흘러가고 있다는 뜻임.
        // Warning Log를 띄우고, 클라이언트에게 새로고침을 요구해야함.
        // 게임 진행이 멈추진 않지만, 흐름이 완전히 꼬여버릴것. ex) A차례인데 B가 건설하는 등의 상황 발생 가능.
        if (type != waitingType)
        {
            Debug.LogWarning("클라이언트에서 기대하는 type과 서버에서 보내준 브로드캐스트 메시지의 type이 일치하지 않습니다. 정상 진행을 위해 반드시 새로고침을 하세요");
            // 새로고침을 강제로 하지는 않고, 유저에게 알림만 보냄.
        }

         switch (type)
        {
            case GameMessageType.BUILT:
            {
                BuiltBroadcast builtBroadcast =
                    JsonConvert.DeserializeObject<BuiltBroadcast>(json);

                OnBuilt?.Invoke(builtBroadcast.playerId, builtBroadcast.propertyId, builtBroadcast.isAccept);
                break;
            }
            case GameMessageType.CARD_DRAWN:
            {
                CardDrawnBroadcast cardDrawnBroadcast =
                    JsonConvert.DeserializeObject<CardDrawnBroadcast>(json);
                
                OnCardDrawn?.Invoke(cardDrawnBroadcast.playerId, cardDrawnBroadcast.cardId);
                break;
            }
            case GameMessageType.DESTINATION_CHOSEN:
            {
                DestinationChosenBroadcast destinationChosenBroadcast =
                    JsonConvert.DeserializeObject<DestinationChosenBroadcast>(json);
                
                OnDestinationChosen?.Invoke(destinationChosenBroadcast.playerId, destinationChosenBroadcast.destinationPosition);
                break;
            }
            case GameMessageType.DICE_ROLLED:
            {
                DiceRolledBroadcast diceRolledBroadcast =
                    JsonConvert.DeserializeObject<DiceRolledBroadcast>(json);
                
                OnDiceRolled?.Invoke(diceRolledBroadcast.playerId, diceRolledBroadcast.dice1, diceRolledBroadcast.dice2);
                break;
            }
            case GameMessageType.PROPERTIES_SOLD:
            {
                PropertiesSoldBroadcast propertiesSoldBroadcast =
                    JsonConvert.DeserializeObject<PropertiesSoldBroadcast>(json);
                
                OnPropertiesSold?.Invoke(propertiesSoldBroadcast.playerId, propertiesSoldBroadcast.propertyIds);
                break;
            }
            case GameMessageType.PROPERTY_ACQUIRED:
            {
                PropertyAcquiredBroadcast propertyAcquiredBroadcast =
                    JsonConvert.DeserializeObject<PropertyAcquiredBroadcast>(json);
                
                OnPropertyAcquired?.Invoke(propertyAcquiredBroadcast.playerId, propertyAcquiredBroadcast.propertyId, propertyAcquiredBroadcast.isAccept);
                break;
            }
            case GameMessageType.PROPERTY_PURCHASED:
            {
                PropertyPurchasedBroadcast propertyPurchasedBroadcast =
                    JsonConvert.DeserializeObject<PropertyPurchasedBroadcast>(json);
                
                OnPropertyPurchased?.Invoke(propertyPurchasedBroadcast.playerId, propertyPurchasedBroadcast.propertyId, propertyPurchasedBroadcast.isAccept);
                break;
            }
            case GameMessageType.GAME_ERROR:
            {
                GameErrorResponse gameErrorResponse =
                    JsonConvert.DeserializeObject<GameErrorResponse>(json);
                    
                Debug.LogError($"에러 메시지 수신. code: {gameErrorResponse.code}, message: {gameErrorResponse.message}");
                Debug.Log($"GameState 동기화 요청을 서버에게 보내야합니다.");
                break;
            }
            default:
            {
                Debug.LogError($"식별할 수 없는 type입니다.: {type}");
                break;
            }
        }
    }

    // GameNetwork는 GameManager의 존재를 몰라도 되게 하기 위해서, GameManager의 함수를 직접 호출하는 대신, 이벤트 방식을 사용할것이다.
    public event Action<long, int, bool> OnBuilt; // GameManager의 OnEnable에서  GameNetwork.Instance.OnDiceRolled += HandleDiceRolled; 하면 된다.
    public event Action<long, int> OnCardDrawn;
    public event Action<long, int> OnDestinationChosen;
    public event Action<long, int, int> OnDiceRolled;
    public event Action<long, List<int>> OnPropertiesSold;
    public event Action<long, int, bool> OnPropertyAcquired;
    public event Action<long, int, bool> OnPropertyPurchased;
   
    public void SendBuildRequest(int propertyId, bool isAccept)
    {
        PropertyDecisionRequest message = new PropertyDecisionRequest
        {
            type = "BUILD",
            propertyId = propertyId,
            isAccept = isAccept
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
    
    public void SendDrawCardRequest()
    {
        DrawCardRequest message = new DrawCardRequest
        {
            type = "DRAW_CARD"
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
    
    public void SendChooseDestinationRequest(int destinationPosition)
    {
        ChooseDestinationRequest message = new ChooseDestinationRequest
        {
            type = "CHOOSE_DESTINATION",
            destinationPosition = destinationPosition
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
    
    public void SendRollDiceRequest()
    {
        RollDiceRequest message = new RollDiceRequest
        {
            type = "ROLL_DICE"
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }

    public void SendSellPropertiesRequest(List<int> propertyIds)
    {
        SellPropertiesRequest message = new SellPropertiesRequest
        {
            type = "SELL_PROPERTIES",
            propertyIds = propertyIds
        };
        
        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
    
    public void SendAcquirePropertyRequest(int propertyId, bool isAccept)
    {
        PropertyDecisionRequest message = new PropertyDecisionRequest
        {
            type = "ACQUIRE_PROPERTY",
            propertyId = propertyId,
            isAccept = isAccept
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
    
    public void SendPurchasePropertyRequest(int propertyId, bool isAccept)
    {
        PropertyDecisionRequest message = new PropertyDecisionRequest
        {
            type = "PURCHASE_PROPERTY",
            propertyId = propertyId,
            isAccept = isAccept
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
    
    public event Action<GameState> OnSync;
    
    /// <summary>
    /// 새로고침 응답을 받았을 때의 특수 처리 함수
    /// </summary>
    private void HandleGameStateSync(string json)
    {
        // 정상적인 GameState가 왔는지 확인
        JObject jsonObject = JObject.Parse(json);
        if (jsonObject["gameState"] == null)
        {
            Debug.Log("[GameNetwork] 서버측 GameState가 아직 준비되지 않았습니다. 3초 뒤 재요청합니다.");
            Invoke("SendGameStateRequest", 3f);
            return;
        }
        
        Debug.Log("[GameNetwork] 싱크 응답 수신. 큐를 초기화하고 상태를 강제 동기화합니다.");
        GameState serverGameState = jsonObject["gameState"]?.ToObject<GameState>();
        
        // 메시지 큐 Clear
        lock (messageQueue)
        {
            messageQueue.Clear();
        }

        Debug.Log("before OnSync?.Invoke");
        OnSync?.Invoke(serverGameState);
    }
    
    public void SendGameStateRequest()
    {
        GameStateRequest message = new GameStateRequest
        {
            type = "GAME_STATE_REQUEST"
        };

        string json = JsonConvert.SerializeObject(message);
        SendGameMessage(json);
    }
}

public class GameServerMessage
{
    public GameMessageType Type;
    public string Json;
}


