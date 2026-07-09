using DiscordRPC;
using DiscordRPC.Logging;
using System;

namespace Switch_Toolbox_Library
{
    /**
     * Code from the DiscordRpc examples:
     * https://github.com/Lachee/discord-rpc-csharp#usage
     */
    public sealed class DiscordPresence : IDisposable
    {
        private readonly object sync = new object();
        private DiscordRpcClient client;
        private string details = "Idle";
        private string state;
        public static DiscordPresence Current { get; private set; }
        public string ClientID = "517901453935771668";

        public void Initialize()
        {
            lock (sync)
            {
                if (client != null && !client.Disposed)
                    return;

                Current = this;
                client = new DiscordRpcClient(ClientID);
                client.Logger = new ConsoleLogger() { Level = LogLevel.Warning };

                client.OnReady += (sender, e) =>
                {
                    Console.WriteLine("Received Ready from user {0}", e.User.Username);
                };

                client.OnPresenceUpdate += (sender, e) =>
                {
                    Console.WriteLine("Received Update! {0}", e.Presence);
                };

                if (!client.Initialize())
                {
                    client.Dispose();
                    client = null;
                    return;
                }

                UpdatePresence();
            }
        }

        public void SetActivity(string fileName, bool developmentBuild)
        {
            lock (sync)
            {
                string nextDetails = string.IsNullOrWhiteSpace(fileName) ? "Idle" : fileName;
                if (nextDetails.Length > 128)
                    nextDetails = nextDetails.Substring(0, 128);
                string nextState = developmentBuild ? "Development Build" : null;

                if (details == nextDetails && state == nextState)
                    return;

                details = nextDetails;
                state = nextState;

                if (client != null && client.IsInitialized && !client.Disposed)
                    UpdatePresence();
            }
        }

        private void UpdatePresence()
        {
            client.SetPresence(new RichPresence()
            {
                Details = details,
                State = state,
                Assets = new Assets()
                {
                    LargeImageKey = "toolbox-logo",
                    LargeImageText = "Splatoon 3 Toolbox"
                }
            });
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (client == null)
                    return;

                if (!client.Disposed)
                {
                    client.ClearPresence();
                    client.Dispose();
                }
                client = null;
                if (ReferenceEquals(Current, this))
                    Current = null;
            }
        }
    }
}
