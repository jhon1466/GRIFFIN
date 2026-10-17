using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Extensions;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Google.Protobuf;

namespace Griffin.GameServer.Handlers
{
    public sealed class ActivityHandler(IEventSigninService eventSignin, IActivityBackService activityBack, ITableService tableService) : ActivityHandlerBase
    {
        public override void HandlePlanActivity(CS_PlanActivity request, Connection connection)
        {
            ulong now = checked((ulong)connection.GameClockSeconds());
            SC_PlanActivity response = new() { Type = request.Type };

            if (request.Type is 4 or 5 or 13)
            {
                // These request types are PlanData.system values. PlanData's column 3 is
                // an enum; columns 4 and 5 carry the actual open/close Unix timestamps.
                List<PlanData> plans = tableService.GetTable<PlanData>()
                    .Where(row => TableFields.U(row, 2) == request.Type)
                    .Where(row => TableFields.U64(row, 4) <= now || TableFields.U64(row, 4) == 0)
                    .Where(row => TableFields.U64(row, 5) == 0 || now <= TableFields.U64(row, 5))
                    .OrderBy(row => TableFields.U(row, 1))
                    .ToList();
                List<PlanData> upcoming = tableService.GetTable<PlanData>()
                    .Where(row => TableFields.U(row, 2) == request.Type)
                    .Where(row => TableFields.U64(row, 4) > now)
                    .OrderBy(row => TableFields.U64(row, 4))
                    .ThenBy(row => TableFields.U(row, 1))
                    .ToList();

                response.ActiveIds.AddRange(plans.Select(row => TableFields.U(row, 1)));
                response.NextIds.AddRange(upcoming.Select(row => TableFields.U(row, 1)));
            }
            else if (request.Type == 6)
            {
                List<ActivityListData> active = tableService.GetTable<ActivityListData>()
                    .Where(row => TableFields.U64(row, 4) == 0 || TableFields.U64(row, 4) <= now)
                    .Where(row => TableFields.U64(row, 5) == 0 || now <= TableFields.U64(row, 5))
                    .OrderBy(row => TableFields.U(row, 1))
                    .ToList();
                List<ActivityListData> upcoming = tableService.GetTable<ActivityListData>()
                    .Where(row => TableFields.U64(row, 4) > now)
                    .OrderBy(row => TableFields.U64(row, 4))
                    .ThenBy(row => TableFields.U(row, 1))
                    .ToList();

                response.ActiveIds.AddRange(active.Select(row => TableFields.U(row, 1)));
                response.NextIds.AddRange(upcoming.Select(row => TableFields.U(row, 1)));
                response.ActivityPlan.AddRange(active.Select(ToActivityPlan));
                response.WillOpenPlans.AddRange(upcoming.Select(ToActivityPlan));
            }

            // Unknown types deliberately receive an empty typed response. Do not reuse the
            // stale event IDs/dates captured from another account or server season.
            connection.Send(response);
        }

        private static ActivityPlan ToActivityPlan(ActivityListData row)
        {
            ActivityPlan plan = new()
            {
                Id = TableFields.U(row, 1),
                OpenTime = checked((long)TableFields.U64(row, 4)),
                CloseTime = checked((long)TableFields.U64(row, 5)),
            };
            plan.Args.AddRange(TableFields.List(row, 15));
            return plan;
        }
        public override void HandleActivityGetMedium(CS_ActivityGetMedium request, Connection connection)
        {
            // Medium/ad tokens are session-issued by the upstream service. Never reuse a
            // token captured from another account/session.
            connection.Send(new SC_ActivityGetMedium());
        }

        public override void HandleGetActivityAmo(CS_GetActivityAmo request, Connection connection)
        {
                    connection.Send(new SC_GetActivityAmo
                    {
                        OOEJEHGFCLL = { },
                    });
        }

        public override void HandleGetActivityDailyFood(CS_GetActivityDailyFood request, Connection connection)
        {
            // The installed 4.0.5136 tables contain no daily-food schedule. Returning
            // old captured timestamps would advertise already-expired claims.
            connection.Send(new SC_GetActivityDailyFood());
        }

        public override void HandleGetActivityConversion(CS_GetActivityConversion request, Connection connection)
        {
                    connection.Send(new SC_GetActivityConversion
                    {
                        GIOIKFICGAE = new PKPJMDCFFKD
                        {
                            CurrId = 0,
                            FPAEHEGGAIH = false,
                        },
                    });
        }

        public override void HandleActivitySkinBargainInfo(CS_ActivitySkinBargainInfo request, Connection connection)
        {
                    connection.Send(new SC_ActivitySkinBargainInfo
                    {
                        Info = { },
                    });
        }

        public override void HandleActivityLotteryInfo(CS_ActivityLotteryInfo request, Connection connection)
        {
                    connection.Send(new SC_ActivityLotteryInfo()
                    {
                        BNKEKDKPCIE = { }
                    });
        }

        public override void HandleActivityBackOneTimeReward(CS_ActivityBackOneTimeReward request, Connection connection)
        {
            // "Welcome back" popup shown at login (SC_ActivityBackDailyRefresh). The client waits for this (empty) reply before
            // leaving the popup. The official reward is not known, so nothing is granted (no reward duplication either).
            Serilog.Log.Information("ActivityBackOneTimeReward event {EventId} acknowledged; no reward granted.", request.EventId);
            connection.Send(new SC_ActivityBackOneTimeReward());
        }

        public override void HandleActivityBackCheckIn(CS_ActivityBackCheckIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            uint day = activityBack.CheckIn(connection.Account.Uid, messages);
            if (day == 0)
            {
                connection.Refuse(MsgId.MsgCsActivityBackCheckIn, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            messages.Add(new SC_ActivityBackCheckIn { IOLHEKBEICG = day });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleActivityCheckIn(CS_ActivityCheckIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (!eventSignin.TryClaim(connection.Account.Uid, request.EventId, messages))
                return;

            messages.Add(new SC_ActivityCheckIn { EventId = request.EventId });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}

