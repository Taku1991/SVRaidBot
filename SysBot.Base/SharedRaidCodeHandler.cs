using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base
{
    /// <summary>
    /// Handles tracking and distribution of raid codes across projects
    /// </summary>
    public static class SharedRaidCodeHandler
    {
        // Track all active raid messages with a code button
        private static readonly Dictionary<ulong, RaidMessageInfo> _activeRaidMessages = new();
        private static readonly object _lock = new object();

        // Delegate for reaction updates - will be set by the Discord project
        public static Func<ulong, ulong, bool, Task>? UpdateMessageReactionCallback { get; set; }

        public class RaidMessageInfo
        {
            public ulong MessageId { get; set; }
            public ulong ChannelId { get; set; }
            public string RaidCode { get; set; } = string.Empty;

            // Dictionary to store all raid information
            public Dictionary<string, string> RaidInfoDict { get; set; } = new Dictionary<string, string>();
        }

        // Add a new active raid message with raid info dictionary
        public static void AddActiveRaidMessageWithInfoDict(ulong messageId, ulong channelId, string raidCode, Dictionary<string, string> raidInfoDict)
        {
            lock (_lock)
            {
                _activeRaidMessages[messageId] = new RaidMessageInfo
                {
                    MessageId = messageId,
                    ChannelId = channelId,
                    RaidCode = raidCode,
                    RaidInfoDict = raidInfoDict
                };
            }
        }

        // Check if a given message is an active raid message
        public static bool IsActiveRaidMessage(ulong messageId)
        {
            lock (_lock)
            {
                return _activeRaidMessages.ContainsKey(messageId);
            }
        }

        // Get the raid code
        public static string GetRaidCodeForMessage(ulong messageId)
        {
            lock (_lock)
            {
                return _activeRaidMessages.TryGetValue(messageId, out var info) ? info.RaidCode : string.Empty;
            }
        }

        // Get the raid info dictionary
        public static Dictionary<string, string>? GetRaidInfoDict(ulong messageId)
        {
            lock (_lock)
            {
                return _activeRaidMessages.TryGetValue(messageId, out var info) ? info.RaidInfoDict : null;
            }
        }

        // Get all active raid messages
        public static List<RaidMessageInfo> GetAllActiveRaidMessages()
        {
            lock (_lock)
            {
                return _activeRaidMessages.Values.ToList();
            }
        }

        // Clear all raid tracking
        public static void ClearAllRaidTracking()
        {
            lock (_lock)
            {
                _activeRaidMessages.Clear();
            }
        }

        // Method to update reactions on all active messages
        public static async Task UpdateReactionsOnAllMessages(bool isActive, CancellationToken token = default)
        {
            if (UpdateMessageReactionCallback == null)
            {
                LogUtil.LogInfo("Reaction update callback not registered.", nameof(SharedRaidCodeHandler));
                return;
            }

            var activeMessages = GetAllActiveRaidMessages();
            foreach (var message in activeMessages)
            {
                try
                {
                    await UpdateMessageReactionCallback(message.MessageId, message.ChannelId, isActive);
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error updating reaction: {ex.Message}", nameof(SharedRaidCodeHandler));
                }
            }
        }
    }
}