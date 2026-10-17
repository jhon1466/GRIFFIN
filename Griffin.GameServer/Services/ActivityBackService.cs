using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// "Welcome back" event check-in: the 21-day list of <c>BackLoginData</c> (column 3 = day, column 4 = reward of the free track as
    /// item -> amount; column 5 is the paid track). ASSUMPTIONS (no official capture): <c>CS_ActivityBackCheckIn.event_id</c> is not needed
    /// to know the day, one day can be claimed per China-local calendar day in order, the reply carries the claimed day, and
    /// <c>checkin_done</c> / field 6 of the info report today's state / days claimed. The paid track needs a real-money purchase
    /// (<c>BackActivityPaidSignData</c> is sold through store 7504), which this server never charges.
    /// </summary>
    public interface IActivityBackService : IGameService
    {
        CheckInState State(uint uid);

        /// <summary>Claims the next day; returns the claimed day (0 = already claimed today or nothing left) and adds the item updates.</summary>
        uint CheckIn(uint uid, List<IMessage> messages);
    }

    public sealed class CheckInState
    {
        public uint DaysClaimed { get; set; }
        public string LastClaimDate { get; set; } = string.Empty;
    }

    public sealed class ActivityBackService(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : IActivityBackService
    {
        private const string StateKey = "activity.back.checkin";

        public static string Today() => GameDay.Key();

        public CheckInState State(uint uid) => accountState.Get<CheckInState>(uid, StateKey) ?? new CheckInState();

        public uint CheckIn(uint uid, List<IMessage> messages)
        {
            CheckInState state = State(uid);
            IMessage? row = tableService.GetTable<BackLoginData>()
                .Where(r => TableFields.U(r, 3) > 0)
                .OrderBy(r => TableFields.U(r, 1))
                .Skip((int)state.DaysClaimed)
                .FirstOrDefault();
            if (state.LastClaimDate == Today() || row is null)
                return 0;

            foreach ((uint item, uint count) in TableFields.Map(row, 4))
                if (count > 0)
                    rewardService.Grant(uid, item, (int)count, messages);

            state.DaysClaimed++;
            state.LastClaimDate = Today();
            accountState.Set(uid, StateKey, state);
            return TableFields.U(row, 3);
        }
    }
}
