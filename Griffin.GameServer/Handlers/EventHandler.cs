using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Requests of the Event service (event sign-in pass, endless tower); there was no handler at all.</summary>
    public sealed class EventHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : EventHandlerBase
    {
        /// <summary>
        /// Sign-in pass of an event (AMCOBBJNOHN, ActivitySigninBpData: 2 = event, 3 = day, 4 = free reward, 5 = premium reward). The state
        /// lists one entry per day reached (bit 1 = free reward claimed, bit 2 = premium claimed) and field 3 = premium owned. A day is
        /// reached by logging in on a new game day. ASSUMPTION A-192: a private server has no purchases, so the premium track is owned.
        /// </summary>
        public sealed class SigninBpState
        {
            public List<uint> Days { get; set; } = [];
            public string LastDay { get; set; } = string.Empty;
        }

        internal static string SigninBpKey(uint activityId) => $"activity.signinbp.{activityId}";

        internal SigninBpState SigninBp(uint uid, uint activityId)
        {
            SigninBpState state = accountState.Get<SigninBpState>(uid, SigninBpKey(activityId)) ?? new SigninBpState();
            int total = tableService.GetTable<ActivitySigninBpData>().Count(r => r.ActivityId == activityId);
            string today = GameDay.Key();
            if (state.LastDay != today && state.Days.Count < total)
            {
                state.Days.Add(0);
                state.LastDay = today;
                accountState.Set(uid, SigninBpKey(activityId), state);
            }

            return state;
        }

        private static GDPBHBFMBMJ SigninBpProto(uint activityId, SigninBpState state)
        {
            GDPBHBFMBMJ info = new() { IKDMNOBMIFE = activityId, PKDEDLNABOI = true };
            info.Rewards.AddRange(state.Days);
            return info;
        }

        public override void HandleEventGetEventSigninBpInfo(CS_EventGetEventSigninBpInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
                connection.Send(new SC_EventGetEventSigninBpInfo { BNLAGAGNIEA = SigninBpProto(request.IKDMNOBMIFE, SigninBp(uid, request.IKDMNOBMIFE)) });
        }

    }

    public sealed class EventSigninBpHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : EventSigninBpHandlerBase
    {
        private EventHandler Shared => new(accountState, tableService, rewardService);

        public override void HandleEventSigninBpGetReward(CS_EventSigninBpGetReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                EventHandler.SigninBpState state = Shared.SigninBp(uid, request.IKDMNOBMIFE);
                List<ActivitySigninBpData> rows = tableService.GetTable<ActivitySigninBpData>().Where(r => r.ActivityId == request.IKDMNOBMIFE).OrderBy(r => r.IFOLEGDHKNN).ToList();
                // idx >= 1000 asks for every day reached
                IEnumerable<int> days = request.Idx >= 1000 ? Enumerable.Range(0, state.Days.Count) : [(int)request.Idx];
                foreach (int day in days)
                {
                    if (day < 0 || day >= state.Days.Count || day >= rows.Count)
                        continue;
                    bool free = request.MPNGHAKGLDM || request.Idx >= 1000;
                    bool premium = request.KEDKPPDFAHD || request.Idx >= 1000;
                    if (free && (state.Days[day] & 1) == 0)
                    {
                        foreach ((uint item, uint count) in rows[day].Rewards)
                            rewardService.Grant(uid, item, (int)count, messages);
                        state.Days[day] |= 1;
                    }

                    if (premium && (state.Days[day] & 2) == 0)
                    {
                        foreach ((uint item, uint count) in rows[day].PIHBPOANGBH)
                            rewardService.Grant(uid, item, (int)count, messages);
                        state.Days[day] |= 2;
                    }
                }

                accountState.Set(uid, EventHandler.SigninBpKey(request.IKDMNOBMIFE), state);
            }

            messages.Add(new SC_EventSigninBpGetReward { IKDMNOBMIFE = request.IKDMNOBMIFE, Idx = request.Idx, MPNGHAKGLDM = request.MPNGHAKGLDM, KEDKPPDFAHD = request.KEDKPPDFAHD });
            connection.SendAutoEncrypted(messages.ToArray());
        }

    }
}
