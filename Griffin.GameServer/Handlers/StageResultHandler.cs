using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Resolves the client's submitted result against the account's pending table-backed stage.
    /// The client still simulates combat locally; this does not provide server-authoritative battle validation.
    /// </summary>
    public sealed class StageResultHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService,
        IItemUseService itemUse,
        IDailyQuestProgressService questProgress,
        IAchievementService achievements,
        IGunService gunService,
        ILoveService love) : StageResultHandlerBase
    {
        /// <summary>
        /// Winning the together-battle of an affection event (<c>LoveEventData</c> type 2, levels 5 to 7) completes it: the level rises and the
        /// client is told with <c>SC_LoveLevelUpdate</c> (field 2 = level, field 3 = next pending event). ASSUMPTION A-55.
        /// </summary>
        private void CompleteLoveEvent(uint uid, StageHandler.PendingStage? pending, uint stageId, SC_StageResult response, List<Google.Protobuf.IMessage> push)
        {
            lock (AccountLock.For(uid))
            {
                GunEntity? gun = gunService.GetPlayerGuns(uid).FirstOrDefault(g =>
                    (pending is null || pending.TogetherGun == 0 || g.GunId == pending.TogetherGun) && love.Pending(g) is { Type: 2 } e && e.Stage == stageId);
                if (gun is null)
                    return;

                love.Advance(uid, gun, push);
                gunService.Save();
                response.StageOther = new StageOther { Together = new CKAOJMGCFFF { GunId = gun.GunId, KFIAKLNJHMB = gun.LoveLevel } };
                push.Add(new SC_LoveLevelUpdate { GunId = gun.GunId, JLHFICPMAEI = gun.LoveLevel, FDKIHAPHHPD = love.Pending(gun)?.Suffix ?? 0 });
                Log.Information("Love event: account {Uid} gun {Gun} won stage {Stage} -> level {Level}.", uid, gun.GunId, stageId, gun.LoveLevel);
            }
        }

        /// <summary>
        /// The 4.0.5136 client does not put the battle report (<c>stage_progress</c>: the challenges it met, the units...) in
        /// <c>CS_StageResult</c>: it announces how many 65 500-byte chunks it holds (<c>stage_count</c>; <c>count</c> is the replay
        /// record) and waits for the server to ask for each one with <c>SC_StageResultNext</c>; it answers with <c>CS_StageResultNext</c>
        /// {seq_id, data, type} (type 1 = the report, a serialised <c>CS_StageResult.Types.DGCMCOFLOJF</c>; type 0 = the replay record,
        /// never requested here). The report is only complete after the last chunk, so the result is resolved then.
        /// </summary>
        public override void HandleStageResultNext(CS_StageResultNext request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (request.Type == StageResultType.StageResult && Uploads.TryGetValue(uid, out ResultUpload? upload))
            {
                if (request.SeqId == upload.NextSeq)
                {
                    upload.Buffer.Write(request.Data.Span);
                    upload.NextSeq++;
                }

                if (upload.NextSeq < upload.Expected)
                {
                    connection.Send(new SC_StageResultNext { NextSeqId = upload.NextSeq, Type = StageResultType.StageResult });
                    return;
                }

                Uploads.TryRemove(uid, out _);
                StageProgress? progress = null;
                try
                {
                    progress = CS_StageResult.Types.DGCMCOFLOJF.Parser.ParseFrom(upload.Buffer.ToArray()).StageProgress;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Stage result report of account {Uid} could not be parsed ({Bytes} bytes); no challenges counted.", uid, upload.Buffer.Length);
                }

                Resolve(connection, uid, upload.Result, progress);
                return;
            }

            connection.Send(new SC_StageResultNext { NextSeqId = request.SeqId, Type = request.Type });
        }

        private sealed class ResultUpload(BattleResult result, uint expected)
        {
            public BattleResult Result { get; } = result;
            public uint Expected { get; } = expected;
            public uint NextSeq { get; set; }
            public MemoryStream Buffer { get; } = new();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ResultUpload> Uploads = new();

        public override void HandleStageResultConfirm(CS_StageResultConfirm request, Connection connection)
        {
            if (connection.Account is not null)
                accountState.Set<StageHandler.PendingStage?>(connection.Account.Uid, "stage.pending", null);

            connection.Send(new SC_StageResultConfirm());
        }

        public override void HandleStageResult(CS_StageResult request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (request.StageProgress is null && request.StageCount > 0)
            {
                Uploads[uid] = new ResultUpload(request.BattleResult, request.StageCount);
                connection.Send(new SC_StageResultNext { NextSeqId = 0, Type = StageResultType.StageResult });
                return;
            }

            Resolve(connection, uid, request.BattleResult, request.StageProgress);
        }

        /// <summary>
        /// Plot-only stages (StageData column 21 = 3: SL-, AS-, LS-, BS-... 317 of them, no enemies and no objectives): the real client plays the dialogue
        /// and sends NOTHING afterwards (no StageInto, no StageResult), so the stage never counted as passed and the next one stayed locked. The
        /// stage is therefore passed when it is started: record, first-clear reward, achievements and the update of the record, but no battle result
        /// screen. ASSUMPTION A-63 (seen in the live log: StageStart 10281 followed only by ClientDataUpload and Sync).
        /// </summary>
        public void CompletePlotStage(Connection connection, uint uid)
        {
            StageHandler.PendingStage? pending = accountState.Get<StageHandler.PendingStage>(uid, "stage.pending");
            Resolve(connection, uid, BattleResult.Win, null, announce: false);

            // The map of the chapter is hidden while the plot plays and does not redraw itself when it comes back (the next stage looked locked until
            // the screen was left and entered again): the record is sent again after the next requests of the client for a while (A-64).
            var records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
            if (pending is not null && records.TryGetValue(pending.StageId.ToString(), out StageRecord? record)
                && tableService.GetTable<StageData>().FirstOrDefault(row => row.Id == pending.StageId) is { } stage)
            {
                // The chapter screen redraws its map when it comes back from the plot only if it was told (event 1105) that the chapter changed: the client
                // raises it when it receives SC_ChapterHeroMode, whose list may be empty. The ticks and unlocks of the nodes are drawn when the screen
                // rebuilds, not on a record update (A-69).
                SC_ChapterHeroMode refresh = new();
                connection.KeepSending(
                    [new SC_StageRecordUpdate { Type = (int)TableFields.U(stage, 20), Record = record }, new SC_StageRecordUpdateBatch { Record = { record } }, refresh],
                    TimeSpan.FromMinutes(3));
            }
        }

        private void Resolve(Connection connection, uint uid, BattleResult battleResult, StageProgress? stageProgress, bool announce = true)
        {
            StageHandler.PendingStage? pending = accountState.Get<StageHandler.PendingStage>(uid, "stage.pending");
            uint reportedStageId = stageProgress?.StageInfo?.StcStageId ?? 0;
            uint stageId = pending?.StageId ?? 0;
            // The stage played is the pending one of StageStart (the client reports it in stage_progress.stage_info only after the chunked
            // upload, and may name a sub-stage); a report that names another stage is logged but the pending stage still decides.
            if (reportedStageId != 0 && stageId != 0 && reportedStageId != stageId)
                Log.Warning("Stage result of account {Uid} names stage {Reported} but stage {Pending} is pending; resolving the pending one.", uid, reportedStageId, stageId);
            StageData? stage = stageId == 0
                ? null
                : tableService.GetTable<StageData>().FirstOrDefault(row => row.Id == stageId);
            if (stage is null)
            {
                Log.Warning("Ignoring stage result for account {Uid}: no matching pending stage (reported={ReportedStageId}, pending={PendingStageId}).", uid, reportedStageId, stageId);
                connection.Send(new SC_StageResult { StageId = reportedStageId, BattleResult = BattleResult.Lose });
                return;
            }

            bool won = battleResult == BattleResult.Win;
            var response = new SC_StageResult
            {
                StageId = stage.Id,
                BattleResult = battleResult,
                StageOther = new StageOther(),
            };
            var push = new List<Google.Protobuf.IMessage>();

            var records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
            records.TryGetValue(stage.Id.ToString(), out StageRecord? prior);
            IReadOnlyDictionary<uint, bool> achievementsBefore = won ? achievements.Completed(uid) : new Dictionary<uint, bool>();
            if (won)
            {
                // StageData field 20 = 41 is Directed Research in the installed tables
                // (e.g. 27401, "定向·手枪I"). Its daily quest is condition 201/link 3800.
                switch (TableFields.U(stage, 20))
                {
                    case 41:
                        questProgress.AddCondition(uid, conditionClass: 201, link: "3800");
                        break;
                    case 42:
                        questProgress.AddCondition(uid, conditionClass: 201, link: "21001");
                        break;
                    case 14:
                        questProgress.AddCondition(uid, conditionClass: 209, link: "1098");
                        break;
                }

                bool firstClear = prior is null || prior.FirstPassTime == 0;
                var validChallenges = stageProgress?.Challenges
                    .Where(stage.ChallengeList.Contains)
                    .Distinct()
                    .ToArray() ?? [];

                Dictionary<string, HashSet<uint>> challengeState =
                    accountState.Get<Dictionary<string, HashSet<uint>>>(uid, "stage.challenge_ids") ?? [];
                string stageKey = stage.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!challengeState.TryGetValue(stageKey, out HashSet<uint>? completedChallenges))
                    challengeState[stageKey] = completedChallenges = [];
                uint oldMask = ChallengeMask(stage, completedChallenges);
                completedChallenges.UnionWith(validChallenges);

                var record = prior?.Clone() ?? new StageRecord { StageId = stage.Id };
                record.StageId = stage.Id;
                if (record.FirstPassTime == 0)
                    record.FirstPassTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // ASSUMPTION (copied from the records of the original server kept in StageHandler.ClearedStages): a passed stage also
                // carries field 6 = 1 and field 7 = true.
                if (record.HKEGGELDDEP == 0)
                    record.HKEGGELDDEP = 1;
                record.MCDNPPCAFOK = true;
                // complete_challenges is a BITMASK, not a count (the client counts the set bits for the stars and tests bit j for the
                // j-th entry of StageData.challenge_list on the result screen; the original server's records hold 7 for 3 objectives).
                // Separate runs accumulate: an objective met once stays met.
                record.CompleteChallenges = ChallengeMask(stage, completedChallenges);
                // old_ccs / new_ccs of SC_StageResult (fields 10 / 11): ASSUMPTION (no official capture) that they are the objective masks
                // before and after this clear; the result screen only lit the objectives it saw change while both were 0.
                response.OldCcs = oldMask;
                response.NewCcs = record.CompleteChallenges;
                accountState.Set(uid, "stage.challenge_ids", challengeState);
                records[stage.Id.ToString()] = record;
                accountState.Set(uid, "stage.records", records);
                // Type = the stage type (StageData col 20): the client files its records per type (11, 12, 13...) and looks a stage up
                // under its own type, so a record update with another type is invisible to the result screen.
                SC_StageRecordUpdate recordUpdate = new() { Type = (int)TableFields.U(stage, 20), Record = record };
                if (announce)
                    connection.Send(recordUpdate);
                else
                {
                    // A plot stage has no result screen. The client starts the dialogue (and puts the chapter screen on top once, which is when that
                    // screen rebuilds its map if it was told that the chapter changed, event 1105 = SC_ChapterHeroMode) as soon as the first SEQUENCED
                    // message of the request arrives: the record, the batch and that notice go first WITHOUT a sequence, so the data is in place when the
                    // reply triggers the rebuild; the ticks and unlocks are drawn then, not later (A-69/A-70).
                    connection.SendUnsequenced(new Google.Protobuf.IMessage[] { recordUpdate, new SC_StageRecordUpdateBatch { Record = { record } }, new SC_ChapterHeroMode() });
                    connection.Send(recordUpdate);
                }

                GrantRepeatableDrops(uid, stage, response, push);
                CompleteLoveEvent(uid, pending, stage.Id, response, push);

                if (firstClear)
                {
                    foreach ((uint itemId, uint count) in stage.FirstReward)
                    {
                        response.FirstDrop.Add(new UserDropCache { ItemId = itemId, ItemNum = count });
                        rewardService.Grant(uid, itemId, checked((int)count), push);
                    }
                }

                // Clearing the stage a chapter / activity story asks for completes its achievement: tell the client now, so the
                // next chapter (or the past-activity story) opens without logging in again.
                if (achievements.NewlyCompleted(uid, achievementsBefore) is { } unlocked)
                    push.Add(new SC_CommonQuestCountUpdate { Counters = unlocked });
            }

            if (announce)
                connection.Send(response);
            foreach (Google.Protobuf.IMessage message in push)
                connection.Send(message);

            // a plot stage keeps its pending entry: a client (or test) that still sends StageInto/StageResult after it finds the stage
            if (announce)
                accountState.Set<StageHandler.PendingStage?>(uid, "stage.pending", null);
            Log.Information("Stage result resolved for account {Uid}, stage {StageId}: {Result}, first clear={FirstClear}.", uid, stage.Id, battleResult, won && (prior is null || prior.FirstPassTime == 0));
        }

        /// <summary>Bit j set = the j-th objective of <c>StageData.challenge_list</c> (col 14) was met.</summary>
        public static uint ChallengeMask(StageData stage, IReadOnlyCollection<uint> met)
        {
            // The opening stages the original server reported as passed (StageHandler.ClearedStages) keep the objectives of that record.
            uint mask = StageHandler.ClearedStages.Where(s => s.StageId == stage.Id).Select(s => s.Challenges).FirstOrDefault();
            for (int j = 0; j < stage.ChallengeList.Count && j < 32; j++)
                if (met.Contains(stage.ChallengeList[j]))
                    mask |= 1u << j;
            return stage.ChallengeList.Count >= 32 ? mask : mask & ((1u << stage.ChallengeList.Count) - 1);
        }

        /// <summary>
        /// What every victory gives, listed in <c>other_drop</c> of the result screen: one weighted pick of each package of
        /// <c>StageData.normal_drop_list</c> (col 18; <c>DropPackageData</c> entries <c>item:count:weight</c>, count 0 = nothing), the fixed
        /// <c>exp_reward</c> map (col 17) and <c>coin</c> (col 16, ASSUMPTION: gold, item 2). Stars have no item reward of their own
        /// (<c>StageChallengeData</c> has none; the chapter star rewards are claimed in <c>ChapterHandler</c>), so <c>challenge_drop</c> stays
        /// empty. Character/commander experience of the stage (<c>gun_exp</c>, <c>exp</c>) is not applied.
        /// </summary>
        private void GrantRepeatableDrops(uint uid, StageData stage, SC_StageResult response, List<Google.Protobuf.IMessage> push)
        {
            List<(uint Item, int Count)> drops = [];
            foreach (uint package in stage.NormalDropList)
                drops.AddRange(itemUse.RollPackage(package));
            foreach ((uint item, uint count) in stage.ExpReward)
                if (count > 0)
                    drops.Add((item, checked((int)count)));
            if (stage.Coin > 0)
                drops.Add((2, checked((int)stage.Coin)));

            foreach ((uint item, int count) in drops)
            {
                rewardService.Grant(uid, item, count, push);
                response.OtherDrop.Add(new UserDropCache { ItemId = item, ItemNum = (uint)count });
            }
        }
    }
}
