﻿using Newtonsoft.Json;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace Server
{
    public class VRCWS : WebSocketBehavior
    {
        public class Message
        {
            public string Method { get; set; }
            public string Target { get; set; }
            public string Content { get; set; }
            public string Signature { get; set; }
            public DateTime TimeStamp { get; set; } = DateTime.Now;

            public override string ToString()
            {
                return $"{Method} - {Target} - {Content}";
            }
            public T GetContentAs<T>()
            {
                try
                {

                    return JsonConvert.DeserializeObject<T>(Content);
                }
                catch (Exception)
                {
                    return default;
                }
            }
        }

        public class AcceptedMethod
        {
            public string Method { get; set; }
            public bool WorldOnly { get; set; }
            public bool SignatureRequired { get; set; }

            public override string ToString()
            {
                return $"{Method} - {WorldOnly} - {SignatureRequired}";
            }
        }

        public static Dictionary<string, VRCWS> userIDToVRCWS = new Dictionary<string, VRCWS>();

        public string userID;
        public string world;

        public List<AcceptedMethod> acceptableMethods = new List<AcceptedMethod>();

        protected override async void OnOpen()
        {
            if (await RateLimiter.RateLimit("ipconnect:" + Context.Headers.Get("X-Forwarded-For"), 60, 10))
            {
                Program.RateLimits.Inc();
                Sessions.CloseSession(ID, CloseStatusCode.PolicyViolation, "Ratelimited");
            }
            UpdateStats();
            Redis.Increase("Connected");
        }

        protected override async void OnMessage(MessageEventArgs e)
        {
            if (!e.IsText)
            {
                Send(new Message() { Method = "Error", Content = "Invalid Message" });
                Program.RecievedMessages.WithLabels("Invalid").Inc();
                Redis.Increase("Invalid");
                return;
            }
            if (await RateLimiter.RateLimit("message:" + ID, 5, 40))
            {
                SendAsync(new Message() { Method = "Error", Content = "Ratelimited" });
                Program.RecievedMessages.WithLabels("Invalid").Inc();
                Redis.Increase("Ratelimited");
                Program.RateLimits.Inc();
                return;
            }
            if (e.RawData.Length> 5120)//5kb
            {
                SendAsync(new Message() { Method = "Error", Content = "Message to large" });
                Program.RecievedMessages.WithLabels("Invalid").Inc();
                Redis.Increase("Messagetolarge");
                return;
            }
            Message msg;
            try
            {
                msg = JsonConvert.DeserializeObject<Message>(e.Data);
            }
            catch (Exception)
            {
                SendAsync(new Message() { Method = "Error", Content = "Invalid Message" });
                Program.RecievedMessages.WithLabels("Invalid").Inc();
                Redis.Increase("Invalid");
                return;
            }
            Program.RecievedMessages.WithLabels(msg.Method).Inc();
            Redis.Increase("RecievedMessages");
            Redis.Increase($"RecievedMessage:{msg.Method}");
            Console.WriteLine($"<< {userID}: {msg}");
            if (msg.Method == "StartConnection")
            {
                if (msg.Content.Length > 40)
                {
                    Sessions.CloseSession(ID, CloseStatusCode.PolicyViolation, "username to large");
                    return;
                }
                if (userIDToVRCWS.ContainsKey(msg.Content)) {
                    SendAsync(new Message() { Method = "Error", Content = "AlreadyConnected" });
                    return;
                }
                userID = msg.Content;
                userIDToVRCWS[userID] = this;
                Redis.Increase("UniqueConnected", userID);
                SendAsync(new Message() { Method = "Connected" });
                return;
            }

            if (userID == null)
            {
                SendAsync(new Message() { Method = "Error", Content = "StartConnectionFirst" });
                return;
            }
            if (msg.Method == "SetWorld")
            {
                world = msg.Content;
                SendAsync(new Message() { Method = "WorldUpdated" });
            }
            else if (msg.Method == "AcceptMethod")
            {
                var acceptedMethod = msg.GetContentAs<AcceptedMethod>(); 
                if (acceptedMethod == null)
                {
                    SendAsync(new Message() { Method = "Error", Content = "DontTryToCrashTheServer" });
                    return;
                }
                if (acceptableMethods.Count > 1024)
                {
                    SendAsync(new Message() { Method = "Error", Content = "ToManyMethods" });
                    return;
                }
                if (acceptableMethods.Any(x => x.Method == acceptedMethod.Method))
                {
                    SendAsync(new Message() { Method = "Error", Content = "MethodAlreadyExisted" });
                    return;
                }

                acceptableMethods.Add(acceptedMethod);
                SendAsync(new Message() { Method = "MethodsUpdated" });
                UpdateStats();

            }
            else if (msg.Method == "RemoveMethod")
            {
                var acceptedMethod = msg.GetContentAs<AcceptedMethod>();
                if (acceptedMethod == null)
                {
                    SendAsync(new Message() { Method = "Error", Content = "DontTryToCrashTheServer" });
                    return;
                }
                var item = acceptableMethods.FirstOrDefault(x => x.Method == acceptedMethod.Method);
                acceptableMethods.Remove(item);
                SendAsync(new Message() { Method = "MethodsUpdated" });
                UpdateStats();
            }
            else if (msg.Method == "IsOnline")
            {
                if (userIDToVRCWS.ContainsKey(msg.Target)) {
                    SendAsync(new Message() { Method = "OnlineStatus", Target = msg.Target, Content = "Online" });
                }
                else
                {
                    SendAsync(new Message() { Method = "OnlineStatus", Target = msg.Target, Content = "Offline" });
                }
            }
            else if (msg.Method == "DoesUserAcceptMethod")
            {
                msg.Method = msg.Content; // remap
                if (ProxyRequestValid(msg))
                {
                    SendAsync(new Message() { Method = "MethodAccept", Target = msg.Target, Content = msg.Content });
                }
                else
                {
                    SendAsync(new Message() { Method = "MethodDecline", Target = msg.Target, Content = msg.Content });
                }
            }
            else
            {
                ProxyMessage(msg);
            }
        }

        private async void ProxyMessage(Message msg)
        {
            //if (await RateLimiter.RateLimit("message:" + userID, 5, 40))
            //   Error("Ratelimit", null);


            Program.ProxyMessagesAttempt.WithLabels(msg.Method).Inc();
            Redis.Increase("ProxyMessagesAttempt");
            Redis.Increase($"ProxyMessagesAttempt:{msg.Method}");
            if (!userIDToVRCWS.ContainsKey(msg.Target))
            {
                Send(new Message() { Method = "Error", Target = msg.Target, Content = "UserOffline" });
                return;
            }
            var remoteUser = userIDToVRCWS[msg.Target];
            if (!ProxyRequestValid(msg))
            {
                Send(new Message() { Method = "Error", Target = msg.Target, Content = "MethodNotAcepted" });
                return;
            }
            msg.Target = userID;
            remoteUser.SendAsync(msg);
            Program.ProxyMessages.WithLabels(msg.Method).Inc();
            Redis.Increase("ProxyMessages");
            Redis.Increase($"ProxyMessage:{msg.Method}");
        }

        private bool ProxyRequestValid(Message msg)
        {
            if (!userIDToVRCWS.ContainsKey(msg.Target))
            {
                return false;
            }
            var remoteUser = userIDToVRCWS[msg.Target];
            var item = remoteUser.acceptableMethods.FirstOrDefault(x => x.Method == msg.Method);
            if (item == null
                || item.WorldOnly && world != remoteUser.world
                || item.SignatureRequired && String.IsNullOrWhiteSpace(msg.Signature))
            {
                return false;
            }

            return true;
        }

        protected override void OnClose(CloseEventArgs e)
        {
            if (userID == null) return;
            Console.WriteLine($"User {userID} dissconected");
            userIDToVRCWS.Remove(userID);
            UpdateStats();
        }

        protected override void OnError(ErrorEventArgs e)
        {
            if (userID == null) return;
            Console.WriteLine($"User {userID} errored");
            userIDToVRCWS.Remove(userID);
            UpdateStats();
        }

        public void Send(Message msg)
        {
            Program.SendMessages.WithLabels(msg.Method).Inc();
            Console.WriteLine($">> {msg}");
            Send(JsonConvert.SerializeObject(msg));
        }
        public void SendAsync(Message msg)
        {
            Program.SendMessages.WithLabels(msg.Method).Inc();
            Redis.Increase($"SendMessages");
            Redis.Increase($"SendMessage:{msg.Method}");
            Console.WriteLine($">> {msg}");
            
            Send(JsonConvert.SerializeObject(msg));
        }

        public void UpdateStats()
        {
            Program.ActiveWS.Set(Sessions.Count);
            Program.CurrentUsers.Set(userIDToVRCWS.Count);

            Dictionary<string, int> usersPerMethod = new Dictionary<string, int>();
            foreach (var item in userIDToVRCWS)
            {
                foreach (var item2 in item.Value.acceptableMethods)
                {
                    usersPerMethod[item2.Method] = usersPerMethod.GetValueOrDefault(item2.Method, 0) + 1;
                }
            }

            foreach (var item in usersPerMethod)
            {
                Program.ActiveMethods.WithLabels(item.Key).Set(item.Value);
            }

        }
    }

    public class Program
    {

        public static readonly Gauge ActiveWS = Metrics.CreateGauge("vrcws_active_ws_current", "Active web sockets");
        public static readonly Gauge RateLimits = Metrics.CreateGauge("vrcws_ratelimit_hit", "Rate Limit hits");
        public static readonly Gauge CurrentUsers = Metrics.CreateGauge("vrcws_active_users_current", "Active users");
        public static readonly Counter SendMessages = Metrics.CreateCounter("vrcws_send_messages", "Messages send", new CounterConfiguration { LabelNames = new[] { "method" } });
        public static readonly Counter RecievedMessages = Metrics.CreateCounter("vrcws_recieved_messages", "Messages recieved", new CounterConfiguration { LabelNames = new[] { "method" } });
        public static readonly Counter ProxyMessages = Metrics.CreateCounter("vrcws_proxy_messages", "Messages proxied", new CounterConfiguration { LabelNames = new[] { "method" } });
        public static readonly Counter ProxyMessagesAttempt = Metrics.CreateCounter("vrcws_proxy_messages_attempt", "Messages proxied attempt", new CounterConfiguration { LabelNames = new[] { "method" } });
        public static readonly Gauge ActiveMethods = Metrics.CreateGauge("vrcws_active_users_per_method", "Active Methods per User", new GaugeConfiguration { LabelNames = new[] { "method" } });

        public static void Main(string[] args)
        {
            
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, eventArgs) => {
                eventArgs.Cancel = true;
                exitEvent.Set();
            };
            var wssv = new WebSocketServer("ws://0.0.0.0:8080");
            
            wssv.Log.Output = (_, __) => { }; // disable log
            var server = new MetricServer(9100);
            wssv.AddWebSocketService<VRCWS>("/VRC");
            wssv.AllowForwardedRequest = true;

            Console.WriteLine("Starting Metric service");
            server.Start();
            Console.WriteLine("Starting WS Server");
            wssv.Start();
            Console.WriteLine("Listening");

            Console.WriteLine("Starting Cleanup Task");
            Task.Run(CleanUpTask);

            exitEvent.WaitOne();
            wssv.Stop();
            server.Stop();

        }

        private static void CleanUpTask()
        {
            while (true)
            {
                List<string> ToClean = new List<string>();
                foreach (var item in VRCWS.userIDToVRCWS)
                {
                    if (item.Value.State == WebSocketState.Closed || item.Value.State == WebSocketState.Closing)
                        ToClean.Add(item.Key);
                }
                foreach (var item in ToClean)
                {
                    Console.WriteLine($"[CleanUpTask] Cleaning {item}");
                    VRCWS.userIDToVRCWS.Remove(item);
                }

                Thread.Sleep(1000 * 60);
            }
        } 
    }
}
