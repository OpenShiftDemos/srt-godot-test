using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using Amqp;
using Amqp.Framing;
using Amqp.Types;
using ProtoBuf;
using redhatgamedev.srt;

public class Server : Node
{
  CSLogger cslogger;

  // TODO: make config file
  String url = "amqp://10.88.0.10:5672";
  String commandInQueue = "COMMAND.IN";
  String gameEventOutQueue = "GAME.EVENT.OUT";

  ConnectionFactory factory;   
  Connection amqpConnection;
  Session amqpSession;
  // for sending game events to all clients
  SenderLink gameEventOutSender;

  // for receiving updates from clients
  ReceiverLink commandInReceiver;

  // for debug sending updates
  SenderLink commandInSender;

  [Export]
  Dictionary<String, Area2D> playerObjects = new Dictionary<string, Area2D>();

  void InstantiatePlayer(String UUID)
  {
    PackedScene playerScene = (PackedScene)ResourceLoader.Load("res://Player.tscn");
    Area2D newPlayer = (Area2D)playerScene.Instance();
    playerObjects.Add(UUID, newPlayer);

    Label playerIDLabel = (Label)newPlayer.GetNode("IDLabel");

    // TODO: deal with really long UUIDs
    playerIDLabel.Text = UUID;

    AddChild(newPlayer);
    cslogger.Debug("Added player instance!");
  }

  void ProcessSecurityGameEvent(SecurityCommandBuffer securityCommandBuffer) {
    cslogger.Debug("Processing security command buffer!");
    switch(securityCommandBuffer.Type)
    {
      case SecurityCommandBuffer.SecurityCommandBufferType.Join:
        cslogger.Debug("Player joined!");
        cslogger.Info($"UUID: {securityCommandBuffer.Uuid}");
        InstantiatePlayer(securityCommandBuffer.Uuid);
        break;
      case SecurityCommandBuffer.SecurityCommandBufferType.Leave:
        cslogger.Debug("Player is leaving!");
        cslogger.Info($"UUID: {securityCommandBuffer.Uuid}");
        break;
    }
  }
  void GameEventReceived(IReceiverLink receiver, Message message)
  {
    cslogger.Debug("Event received!");
    // accept the message so that it gets removed from the queue
    receiver.Accept(message);

    byte[] binaryBody = (byte[])message.Body;

    MemoryStream st = new MemoryStream(binaryBody, false);

    // prep a command buffer for processing the message
    CommandBuffer commandBuffer;
    commandBuffer = Serializer.Deserialize<CommandBuffer>(st);

    switch(commandBuffer.Type)
    {
      case CommandBuffer.CommandBufferType.Security:
        cslogger.Debug("Security event!");
        ProcessSecurityGameEvent(commandBuffer.securityCommandBuffer);
        break;
      case CommandBuffer.CommandBufferType.Rawinput:
        cslogger.Debug("Raw input event!");
        break;
    }
  }

  async void InitializeAMQP()
  {
    // TODO: should probably wrap in some kind of try and catch failure to connect?
    //       is this even async?
    // TODO: include connection details
    cslogger.Debug("Initializing AMQP connection");
    Connection.DisableServerCertValidation = true;

    //Trace.TraceLevel = TraceLevel.Frame;
    //Trace.TraceListener = (l, f, a) => Console.WriteLine(DateTime.Now.ToString("[hh:mm:ss.fff]") + " " + string.Format(f, a));
    factory = new ConnectionFactory();

    Address address = new Address(url);
    amqpConnection = await factory.CreateAsync(address);

    //Connection connection = new Connection(address);
    amqpSession = new Session(amqpConnection);

    // topics are multicast
    // queues are anycast
    // https://stackoverflow.com/a/51595195

    // multicast topic for the server to send game event updates to clients
    Target gameEventOutTarget = new Target
    {
      Address = gameEventOutQueue,
      Capabilities = new Symbol[] { new Symbol("topic") }
    };
    gameEventOutSender = new SenderLink(amqpSession, "srt-game-server-sender", gameEventOutTarget, null);

    // anycast queue for the server to receive events from clients
    Source commandInSource = new Source
    {
      Address = commandInQueue,
      Capabilities = new Symbol[] { new Symbol("queue") }
    };
    commandInReceiver = new ReceiverLink(amqpSession, "srt-game-server-receiver", commandInSource, null);
    commandInReceiver.Start(10, GameEventReceived);

    Target commandInTarget = new Target
    {
      Address = commandInQueue,
      Capabilities = new Symbol[] { new Symbol("queue") }
    };
    commandInSender = new SenderLink(amqpSession, "srt-game-server-debug-sender", commandInTarget, null);

    cslogger.Debug("Finished initializing AMQP connection");
  }

  void _on_JoinAPlayer_pressed()
  {
    LineEdit textField = GetNode<LineEdit>("PlayerID");
    cslogger.Debug($"Sending join with UUID: {textField.Text}");


    // construct a join message from the text in the debug field
    CommandBuffer cb = new CommandBuffer();
    cb.Type = CommandBuffer.CommandBufferType.Security;

    SecurityCommandBuffer scb = new SecurityCommandBuffer();
    scb.Uuid = textField.Text;  
    scb.Type = SecurityCommandBuffer.SecurityCommandBufferType.Join;

    cb.securityCommandBuffer = scb;

    // TODO: should make this a function since we use it a lot
    // serialize it into a byte stream
    MemoryStream st = new MemoryStream();
    Serializer.Serialize<CommandBuffer>(st, cb);

    byte[] msgBytes = st.ToArray();

    Message msg = new Message(msgBytes);

    // don't care about the ack on our message being received
    commandInSender.Send(msg, null, null);
    //commandInSender.Send(msg);
  }

  // Called when the node enters the scene tree for the first time.
  public override void _Ready()
  {
    // initialize the logging configuration
    Node gdlogger = GetNode<Node>("/root/GDLogger");
    gdlogger.Call("load_config", "res://logger.cfg");
    cslogger = GetNode<CSLogger>("/root/CSLogger");

    cslogger.Info("Space Ring Things (SRT) Game Server");
    InitializeAMQP(); 

    cslogger.Info("Beginning game server");
    // TODO: output the current config


  }

  // Called every frame. 'delta' is the elapsed time since the previous frame.
  public override void _Process(float delta)
  {

    // look for any inputs to then combine with the UUID in the text box and 
    // subsequently sent a control message
    var velocity = Vector2.Zero; // The player's movement vector.

    if (Input.IsActionPressed("rotate_right"))
    {
        velocity.x += 1;
    }

    if (Input.IsActionPressed("rotate_left"))
    {
        velocity.x -= 1;
    }

    if (Input.IsActionPressed("thrust_forward"))
    {
        velocity.y += 1;
    }

    if (Input.IsActionPressed("thrust_reverse"))
    {
        velocity.y -= 1;
    }

    if (velocity.Length() > 0)
    {
      // fetch the UUID from the text field to use in the message
      LineEdit textField = GetNode<LineEdit>("PlayerID");

      // there was some kind of input, so construct a message to send to the server
      CommandBuffer cb = new CommandBuffer();
      cb.Type = CommandBuffer.CommandBufferType.Rawinput;

      RawInputCommandBuffer ricb = new RawInputCommandBuffer();
      ricb.Type = RawInputCommandBuffer.RawInputCommandBufferType.Dualstick;
      ricb.Uuid = textField.Text;

      DualStickRawInputCommandBuffer dsricb = new DualStickRawInputCommandBuffer();

      Box2d.PbVec2 b2d = new Box2d.PbVec2();
      b2d.X = velocity.x;
      b2d.Y = velocity.y;

      dsricb.pbv2Move = b2d;
      ricb.dualStickRawInputCommandBuffer = dsricb;

      cb.rawInputCommandBuffer = ricb;

      // serialize it into a byte stream
      MemoryStream st = new MemoryStream();
      Serializer.Serialize<CommandBuffer>(st, cb);

      byte[] msgBytes = st.ToArray();

      Message msg = new Message(msgBytes);

      // don't care about the ack on our message being received
      commandInSender.Send(msg, null, null);
      //commandInSender.Send(msg);
    }
  }
}
