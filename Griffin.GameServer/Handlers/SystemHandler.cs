using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class SystemHandler(ITableService tableService) : SystemHandlerBase
    {
        private const uint NotReleasedCondition = 99999;

        /// <summary>
        /// Functions kept closed on purpose (none now). 27000, the stamina purchase dialog, was listed here while it crashed the client with a
        /// stack overflow; the cause was the control room's timestamps being sent in the real clock instead of the client's (see
        /// ControlRoomHandler.ToGame), so it is open again. Put an id here to answer its button with "function not open".
        /// </summary>
        private static readonly HashSet<uint> NeverOpen = [];

        public override void HandleSystemUnlocks(CS_SystemUnlocks request, Connection connection)
        {
            SC_SystemUnlocks scSystemUnlocks = new SC_SystemUnlocks()
            {
                SystemLock = new SystemUnlock
                {
                    Version = 1085571,
                },
            };

            // Build both maps from the installed client data. The older account capture also
            // opened 38002, which this table marks as condition 99999 (not released) and removed.
            foreach (UnlockData unlock in tableService.GetTable<UnlockData>())
            {
                if (TableFields.List(unlock, 3).Contains(NotReleasedCondition) || NeverOpen.Contains(unlock.Id))
                    continue;

                scSystemUnlocks.SystemLock.Unlocks[unlock.Id] = true;
                ulong unlockTime = TableFields.U64(unlock, 12);
                if (unlockTime > 0 && unlockTime <= long.MaxValue)
                    scSystemUnlocks.SystemTime[unlock.Id] = (long)unlockTime;
            }

            connection.SendAutoEncrypted(scSystemUnlocks);
        }
    }
}
