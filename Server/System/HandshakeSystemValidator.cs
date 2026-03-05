using LmpCommon.Enums;
using Server.Client;
using Server.Command.Command;
using Server.Settings.Structures;
using System.Linq;
using System.Text.RegularExpressions;

namespace Server.System
{
    public partial class HandshakeSystem
    {
        public static bool PlayerNameIsValid(string playerName, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrEmpty(playerName))
            {
                reason = "Username too short. Min chars: 1";
                return false;
            }

            if (playerName.Length > GeneralSettings.SettingsStore.MaxUsernameLength)
            {
                reason = $"Username too long. Max chars: {GeneralSettings.SettingsStore.MaxUsernameLength}";
                return false;
            }

            var regex = new Regex(@"^[-_a-zA-Z0-9]+$"); // Regex to only allow alphanumeric, dashes and underscore
            if (!regex.IsMatch(playerName))
            {
                reason = "Invalid username characters (only A-Z, a-z, numbers, - and _)";
                return false;
            }

            return true;
        }

        private bool CheckUsernameLength(ClientStructure client, string username)
        {
            if (!PlayerNameIsValid(username, out var reason))
            {
                if (reason.Contains("long") || reason.Contains("short"))
                {
                    Reason = reason;
                    HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.InvalidPlayername, Reason);
                    return false;
                }
            }

            return true;
        }

        private bool CheckServerFull(ClientStructure client)
        {
            if (ClientRetriever.GetActiveClientCount() >= GeneralSettings.SettingsStore.MaxPlayers)
            {
                Reason = "Server full";
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.ServerFull, Reason);
                return false;
            }
            return true;
        }

        private bool CheckPlayerIsBanned(ClientStructure client, string uniqueId)
        {
            if (BanPlayerCommand.GetBannedPlayers().Contains(uniqueId))
            {
                Reason = "Banned";
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.PlayerBanned, Reason);
                return false;
            }
            return true;
        }

        private bool CheckUsernameIsReserved(ClientStructure client, string playerName)
        {
            if (playerName == "Initial" || playerName == GeneralSettings.SettingsStore.ConsoleIdentifier)
            {
                Reason = "Using reserved name";
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.InvalidPlayername, Reason);
                return false;
            }
            return true;
        }

        private bool CheckPlayerIsAlreadyConnected(ClientStructure client, string playerName)
        {
            var existingClient = ClientRetriever.GetClientByName(playerName);
            if (existingClient != null)
            {
                Reason = "Username already taken";
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.InvalidPlayername, Reason);
                return false;
            }
            return true;
        }

        private bool CheckUsernameCharacters(ClientStructure client, string playerName)
        {
            if (!PlayerNameIsValid(playerName, out var reason))
            {
                if (reason.Contains("characters"))
                {
                    Reason = reason;
                    HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.InvalidPlayername, Reason);
                    return false;
                }
            }
            return true;
        }
    }
}
