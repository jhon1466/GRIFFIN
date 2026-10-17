using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class CheckInHandler(
        IEventSigninService eventSignin,
        IDailyQuestProgressService questProgress,
        IAccountStateService accountState,
        ITableService tableService,
        IRewardService rewardService) : CheckInHandlerBase
    {
        private const string DailyStateKey = "checkin.daily";
        private const uint DailyCheckinId = 1;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> DailyLocks = new();

        public override void HandleGetActivityCheckIn(CS_GetActivityCheckIn request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(eventSignin.GetStatus(connection.Account.Uid));
        }

        public override void HandleGetActivityCheckInAnniversary(CS_GetActivityCheckInAnniversary request, Connection connection)
        {
            connection.Send(new SC_GetActivityCheckInAnniversary
            {
                ActivityCheckin = { },
            });
        }

        public override void HandleGetDailyCheckIn(CS_GetDailyCheckIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            DailyCheckinState state = GetDailyState(connection.Account.Uid);
            connection.Send(new SC_GetDailyCheckIn
            {
                DailyCheckin = new QuestCheckin
                {
                    CheckinDone = state.LastClaimDate == ChinaDateKey(),
                    LastDayCheckin = false,
                    CheckinId = 1,
                    // The client displays CheckinDay + 1 as the next claim slot. Zero is therefore the first day.
                    CheckinDay = ClientCheckinDay(state.CheckinDay),
                },
                Passive = false,
            });
        }

        public override void HandleDailyCheckIn(CS_DailyCheckIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (DailyLocks.GetOrAdd(uid, static _ => new object()))
            {
                DailyCheckinState state = GetDailyState(uid);
                string today = ChinaDateKey();
                if (state.LastClaimDate != today)
                {
                    uint nextDay = NextClaimDay(state.CheckinDay);
                    DailyCheckinData? itemIds = tableService.GetTable<DailyCheckinData>()
                        .FirstOrDefault(row => row.Id == DailyCheckinId);
                    DailyCheckinData? itemCounts = tableService.GetTable<DailyCheckinData>()
                        .FirstOrDefault(row => row.Id == DailyCheckinId + 1);
                    if (itemIds is null || itemCounts is null || nextDay is 0 or > 28)
                    {
                        Serilog.Log.Warning("Daily check-in table rows are missing for check-in {CheckinId}, day {Day}.", DailyCheckinId, nextDay);
                        connection.Send(new SC_DailyCheckIn());
                        return;
                    }

                    int dayField = checked((int)nextDay + 1); // protobuf fields 2..29 are day1..day28
                    uint itemId = TableFields.U(itemIds, dayField);
                    uint itemNum = TableFields.U(itemCounts, dayField);
                    List<Google.Protobuf.IMessage> updates = [];
                    GrantResult grant = itemNum is > 0 and <= int.MaxValue
                        ? rewardService.Grant(uid, itemId, (int)itemNum, updates)
                        : new GrantResult(false);
                    if (!grant.Granted)
                    {
                        Serilog.Log.Warning("Daily check-in reward is not grantable for account {Uid}, check-in {CheckinId}, day {Day}, item {ItemId} x{ItemNum}.",
                            uid, DailyCheckinId, nextDay, itemId, itemNum);
                        connection.Send(new SC_DailyCheckIn());
                        return;
                    }

                    state.CheckinDay = nextDay;
                    state.LastClaimDate = today;
                    accountState.Set(uid, DailyStateKey, state);
                    questProgress.AddCondition(uid, conditionClass: 400);

                    updates.Add(new SC_DailyCheckIn { ItemId = itemId, ItemNum = itemNum });
                    connection.SendAutoEncrypted(updates.ToArray());
                    return;
                }

                connection.Send(new SC_DailyCheckIn());
            }
        }

        private DailyCheckinState GetDailyState(uint uid) =>
            accountState.Get<DailyCheckinState>(uid, DailyStateKey) ?? new DailyCheckinState();

        /// <summary>The client displays its stored completed-day field plus one as the next reward slot.</summary>
        public static uint ClientCheckinDay(uint lastClaimedDay) => lastClaimedDay >= 28 ? 0 : lastClaimedDay;

        public static uint NextClaimDay(uint lastClaimedDay) => lastClaimedDay >= 28 ? 1 : lastClaimedDay + 1;

        private static string ChinaDateKey() =>
            GameDay.Key();
    }

    public sealed class DailyCheckinState
    {
        public string LastClaimDate { get; set; } = string.Empty;
        public uint CheckinDay { get; set; }
    }
}
