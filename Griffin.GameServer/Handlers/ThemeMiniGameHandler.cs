using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    public sealed class ThemeMiniGameHandler(IAccountStateService accountState, Griffin.Common.Table.ITableService tables, IRewardService rewards, IEventTaskService eventTasks) : ThemeMiniGameHandlerBase
    {
        private const string StateKey = "theme-minigame.v1";
        private const string RedPointStateKey = "theme-minigame.red-points.v1";
        private const uint DoodleJumpGameId = 21005;

        /// <summary>"I Wanna" platform challenge of the 擂台挑战 event (A-101): the client plays it locally and reports result, score and stars of the stage.</summary>
        private const uint IwannaGameId = 28001;
        private const string IwannaRecordKey = "theme-minigame.iwanna.v1";

        private sealed record IwannaRecord(uint Score, uint Stars);

        [PacketHandler(MsgId.MsgCsThemeMiniGameGetSkill, MsgId.MsgScThemeMiniGameGetSkill)]
        public override void HandleThemeMiniGameGetSkill(CS_ThemeMiniGameGetSkill request, Connection connection)
        {
            if (connection.Account is null)
                return;
            if (request.FNEFKKLJMDN != ThemeModules.Motorcycle)
            {
                // only the motorcycle game has skills in this protocol (oneof field 4)
                connection.Refuse(MsgId.MsgCsThemeMiniGameGetSkill, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                // Client MLBFMHPHJIG.GIGGKFHJMDK: a skill can be learned when its record is unlocked, the skill of column 9 is learned,
                // it is not learned yet and the points (IJKGNEEBFKJ) reach column 10. The reply carries the points left.
                string key = ThemeMiniGameStages.Key(request.ThemeId, request.FNEFKKLJMDN);
                ThemeMiniGameStages.State state = accountState.Get<ThemeMiniGameStages.State>(uid, key) ?? new();
                var skill = ThemeMiniGameStages.MotorcycleSkills(tables, request.ThemeId).FirstOrDefault(s => s.Id == request.JJOKKNFDAGE);
                if (skill.Id == 0 || state.Skills.Contains(skill.Id) || !ThemeMiniGameStages.UnlockMet(tables, state, skill.Unlock)
                    || (skill.Previous != 0 && !state.Skills.Contains(skill.Previous)))
                {
                    connection.Refuse(MsgId.MsgCsThemeMiniGameGetSkill, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }
                if (state.Points < skill.Cost)
                {
                    connection.Refuse(MsgId.MsgCsThemeMiniGameGetSkill, HNINDAOJCKP.MiniGameSkillItemIsNotEnough);
                    return;
                }

                state.Points -= skill.Cost;
                state.Skills.Add(skill.Id);
                accountState.Set(uid, key, state);
                connection.Send(new SC_ThemeMiniGameGetSkill
                {
                    ThemeId = request.ThemeId,
                    FNEFKKLJMDN = request.FNEFKKLJMDN,
                    JJOKKNFDAGE = request.JJOKKNFDAGE,
                    Motorcycle = new SC_ThemeMiniGameGetSkill.Types.DDDLLMEJOOI { IJKGNEEBFKJ = state.Points },
                });
            }
        }

        /// <summary>
        /// Client 4.0.5142 never sends this request (no caller of MSG_CS_ThemeMiniGameClaimUltimateRewards) and no table names an
        /// "ultimate" reward of a theme mini game, so it is only acknowledged; nothing is granted.
        /// </summary>
        public override void HandleThemeMiniGameClaimUltimateRewards(CS_ThemeMiniGameClaimUltimateRewards request, Connection connection)
        {
            connection.Send(new SC_ThemeMiniGameClaimUltimateRewards { ThemeId = request.ThemeId, FNEFKKLJMDN = request.FNEFKKLJMDN });
        }

        [PacketHandler(MsgId.MsgCsThemeMiniGameRecordRedPoint, MsgId.MsgScThemeMiniGameRecordRedPoint)]
        public void HandleThemeMiniGameRecordRedPoint(CS_ThemeMiniGameRecordRedPoint request, Connection connection)
        {
            if (connection.Account is null)
                return;

            SC_ThemeMiniGameRecordRedPoint response = new()
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
            };
            if (request.IwannaTouch is { StageId: > 0 } read)
            {
                Dictionary<string, bool> readStages = accountState.Get<Dictionary<string, bool>>(
                    connection.Account.Uid, RedPointStateKey) ?? [];
                readStages[$"{request.ThemeId}:{request.FNEFKKLJMDN}:{read.StageId}"] = true;
                accountState.Set(connection.Account.Uid, RedPointStateKey, readStages);
                response.IwannaTouch = read.Clone();
            }

            connection.Send(response);
        }

        public override void HandleThemeMiniGameInfo(CS_ThemeMiniGameInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            Dictionary<string, ThemeMiniGameState> states =
                accountState.Get<Dictionary<string, ThemeMiniGameState>>(connection.Account.Uid, StateKey) ?? [];
            ThemeMiniGameState state = GetState(states, request.ThemeId, request.FNEFKKLJMDN);

            SC_ThemeMiniGameInfo response = new()
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
            };
            if (ThemeMiniGameStages.IsStageGame(request.FNEFKKLJMDN))
            {
                connection.Send(StageInfo(connection.Account.Uid, request.ThemeId, request.FNEFKKLJMDN));
                return;
            }

            if (request.FNEFKKLJMDN == DoodleJumpGameId)
            {
                response.DoodleJumpData = new FOMAJCHMPFL
                {
                    KNFOFFBOCML = state.Unlocked,
                    CGFMBCCDFPG = state.BestScore,
                    Duration = state.Duration,
                    Times = state.Times,
                };
            }

            if (request.FNEFKKLJMDN == IwannaGameId)
            {
                // iwanna_data (field 9) as the client reads it: {1: map<stage, {1: best score, 2: stars}>, 2: bit mask of the stages whose red dot was read}. The checked-in schema
                // of this oneof is another build's, so the bytes are written by hand and parsed back (the unknown fields travel unchanged).
                Dictionary<string, IwannaRecord> records = accountState.Get<Dictionary<string, IwannaRecord>>(connection.Account.Uid, IwannaRecordKey) ?? [];
                using MemoryStream data = new();
                using (CodedOutputStream output = new(data, leaveOpen: true))
                    foreach ((string stage, IwannaRecord record) in records.Where(pair => uint.TryParse(pair.Key, out _)).OrderBy(pair => uint.Parse(pair.Key)))
                    {
                        using MemoryStream entry = new();
                        using (CodedOutputStream inner = new(entry, leaveOpen: true))
                        {
                            inner.WriteTag(1, WireFormat.WireType.Varint);
                            inner.WriteUInt32(uint.Parse(stage));
                            inner.WriteTag(2, WireFormat.WireType.LengthDelimited);
                            using MemoryStream value = new();
                            using (CodedOutputStream valueOut = new(value, leaveOpen: true))
                            {
                                valueOut.WriteTag(1, WireFormat.WireType.Varint);
                                valueOut.WriteUInt32(record.Score);
                                valueOut.WriteTag(2, WireFormat.WireType.Varint);
                                valueOut.WriteUInt32(record.Stars);
                            }

                            inner.WriteBytes(ByteString.CopyFrom(value.ToArray()));
                        }

                        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
                        output.WriteBytes(ByteString.CopyFrom(entry.ToArray()));
                    }

                using MemoryStream message = new();
                using (CodedOutputStream outer = new(message, leaveOpen: true))
                {
                    outer.WriteTag(1, WireFormat.WireType.Varint);
                    outer.WriteUInt32(request.ThemeId);
                    outer.WriteTag(2, WireFormat.WireType.Varint);
                    outer.WriteUInt32(request.FNEFKKLJMDN);
                    outer.WriteTag(9, WireFormat.WireType.LengthDelimited);
                    outer.WriteBytes(ByteString.CopyFrom(data.ToArray()));
                }

                connection.Send(SC_ThemeMiniGameInfo.Parser.ParseFrom(message.ToArray()));
                return;
            }

            connection.Send(response);
        }

        [PacketHandler(MsgId.MsgCsThemeMiniGameRank, MsgId.MsgScThemeMiniGameRank)]
        public void HandleThemeMiniGameRank(CS_ThemeMiniGameRank request, Connection connection)
        {
            // There is no real friend/guild population for a single-player local server.
            // Return the requested scope with no fabricated players or scores.
            connection.Send(new SC_ThemeMiniGameRank
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                OCGNAONELAA = (SC_ThemeMiniGameRank.Types.IGHPEHHAANA)(int)request.OCGNAONELAA,
            });
        }

        public override void HandleThemeMiniGameStart(CS_ThemeMiniGameStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (ThemeMiniGameStages.IsStageGame(request.FNEFKKLJMDN))
            {
                StageStart(request, connection);
                return;
            }

            if (request.FNEFKKLJMDN == IwannaGameId)
            {
                // the unlock by time of each stage is decided by the client (activity opening + IwannaStageData column 3); the server only opens the run
                connection.Send(new SC_ThemeMiniGameStart { ThemeId = request.ThemeId, FNEFKKLJMDN = request.FNEFKKLJMDN, StageId = request.StageId });
                return;
            }

            if (request.FNEFKKLJMDN != DoodleJumpGameId)
            {
                // any other mini game of an event without stage tables here (butt game...): the run opens and the client plays it locally; its records are not kept (A-101)
                Log.Information("Theme minigame {GameId} start: generic answer for account {Uid}, theme {ThemeId}, stage {StageId}.",
                    request.FNEFKKLJMDN, connection.Account.Uid, request.ThemeId, request.StageId);
                connection.Send(new SC_ThemeMiniGameStart { ThemeId = request.ThemeId, FNEFKKLJMDN = request.FNEFKKLJMDN, StageId = request.StageId });
                return;
            }

            Dictionary<string, ThemeMiniGameState> states = GetStates(connection.Account.Uid);
            string key = Key(request.ThemeId, request.FNEFKKLJMDN);
            ThemeMiniGameState state = GetState(states, request.ThemeId, request.FNEFKKLJMDN) with
            {
                Unlocked = true,
                StageId = request.StageId,
                StartedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            states[key] = state;
            accountState.Set(connection.Account.Uid, StateKey, states);

            connection.Send(new SC_ThemeMiniGameStart
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                StageId = request.StageId,
            });
        }

        [PacketHandler(MsgId.MsgCsThemeMiniGameFinish, MsgId.MsgScThemeMiniGameFinish)]
        public void HandleThemeMiniGameFinish(CS_ThemeMiniGameFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (ThemeMiniGameStages.IsStageGame(request.FNEFKKLJMDN))
            {
                StageFinish(request, connection);
                return;
            }

            if (request.FNEFKKLJMDN == IwannaGameId)
            {
                // inner message of the report and of the answer: {1: result (1 win, 2 lose), 2: score, 3: stars}
                uint result = 0, score = 0, stars = 0, tag;
                CodedInputStream input = new(request.Iwanna?.ToByteArray() ?? []);
                while ((tag = input.ReadTag()) != 0)
                {
                    switch (tag >> 3)
                    {
                        case 1: result = input.ReadUInt32(); break;
                        case 2: score = input.ReadUInt32(); break;
                        case 3: stars = input.ReadUInt32(); break;
                        default: input.SkipLastField(); break;
                    }
                }

                Dictionary<string, IwannaRecord> records = accountState.Get<Dictionary<string, IwannaRecord>>(connection.Account.Uid, IwannaRecordKey) ?? [];
                string stageKey = request.StageId.ToString();
                IwannaRecord known = records.GetValueOrDefault(stageKey) ?? new IwannaRecord(0, 0);
                if (result == 1)
                    records[stageKey] = new IwannaRecord(Math.Max(known.Score, score), Math.Max(known.Stars, stars));
                accountState.Set(connection.Account.Uid, IwannaRecordKey, records);

                using MemoryStream answer = new();
                using (CodedOutputStream inner = new(answer, leaveOpen: true))
                {
                    inner.WriteTag(1, WireFormat.WireType.Varint);
                    inner.WriteUInt32(result);
                    inner.WriteTag(2, WireFormat.WireType.Varint);
                    inner.WriteUInt32(score);
                    inner.WriteTag(3, WireFormat.WireType.Varint);
                    inner.WriteUInt32(stars);
                }

                SC_ThemeMiniGameFinish reply = new()
                {
                    ThemeId = request.ThemeId,
                    FNEFKKLJMDN = request.FNEFKKLJMDN,
                    StageId = request.StageId,
                    Score = request.Score,
                    Iwanna = SC_ThemeMiniGameFinish.Types.IJIBOJHKFKA.Parser.ParseFrom(answer.ToArray()),
                };
                connection.Send(reply);
                return;
            }

            if (request.FNEFKKLJMDN != DoodleJumpGameId)
            {
                Log.Information("Theme minigame {GameId} finish: generic answer for account {Uid}, theme {ThemeId}, stage {StageId}, score {Score}.",
                    request.FNEFKKLJMDN, connection.Account.Uid, request.ThemeId, request.StageId, request.Score);
                connection.Send(new SC_ThemeMiniGameFinish
                {
                    ThemeId = request.ThemeId,
                    FNEFKKLJMDN = request.FNEFKKLJMDN,
                    StageId = request.StageId,
                    Score = request.Score,
                    CGFMBCCDFPG = request.Score,
                });
                return;
            }

            Dictionary<string, ThemeMiniGameState> states = GetStates(connection.Account.Uid);
            string key = Key(request.ThemeId, request.FNEFKKLJMDN);
            ThemeMiniGameState state = GetState(states, request.ThemeId, request.FNEFKKLJMDN);
            if (state.StartedAtUnixSeconds == 0 || state.StageId != request.StageId)
            {
                Log.Warning("Theme minigame finish rejected for account {Uid}: no matching start for theme {ThemeId}, stage {StageId}.",
                    connection.Account.Uid, request.ThemeId, request.StageId);
                connection.Refuse(MsgId.MsgCsThemeMiniGameFinish, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            // Doodle Jump runs in the client's local simulation. Keep the score it reports, but
            // do not mint rewards or unlocks from it; those require a proven server-side rule.
            state = state with
            {
                BestScore = Math.Max(state.BestScore, request.Score),
                Duration = request.Duration,
                Times = state.Times == uint.MaxValue ? uint.MaxValue : state.Times + 1,
                StageId = 0,
                StartedAtUnixSeconds = 0,
            };
            states[key] = state;
            accountState.Set(connection.Account.Uid, StateKey, states);

            connection.Send(new SC_ThemeMiniGameFinish
            {
                ThemeId = request.ThemeId,
                FNEFKKLJMDN = request.FNEFKKLJMDN,
                StageId = request.StageId,
                Score = request.Score,
                CGFMBCCDFPG = state.BestScore,
            });
        }

        private SC_ThemeMiniGameInfo StageInfo(uint uid, uint theme, uint module)
        {
            ThemeMiniGameStages.State state = accountState.Get<ThemeMiniGameStages.State>(uid, ThemeMiniGameStages.Key(theme, module)) ?? new();
            SC_ThemeMiniGameInfo reply = new() { ThemeId = theme, FNEFKKLJMDN = module };
            IReadOnlyList<ThemeMiniGameStages.Stage> stages = ThemeMiniGameStages.Stages(tables, theme, module);
            // Record field 1 = cleared (ASSUMPTION: the client only reads it as a flag), field 2 = unlocked (the client opens the stage from it).
            switch (module)
            {
                case ThemeModules.CandyCatch:
                    reply.CandyCatchData = new IHNLKCLJMDC();
                    foreach (var stage in stages)
                    {
                        var rec = state.Stages.GetValueOrDefault(stage.Id) ?? new();
                        reply.CandyCatchData.KNMAKEJFGDK[stage.Id] = new IHNLKCLJMDC.Types.NLEKECNKFOP
                        {
                            KNFOFFBOCML = rec.Cleared, MPNGHAKGLDM = ThemeMiniGameStages.Unlocked(tables, state, stage), CGFMBCCDFPG = rec.Best, KNAHJGMNKIL = rec.Stars,
                        };
                    }
                    break;
                case ThemeModules.CardFlip:
                    reply.CardFlipData = new HGNOJKGKMNH();
                    foreach (var stage in stages)
                    {
                        var rec = state.Stages.GetValueOrDefault(stage.Id) ?? new();
                        reply.CardFlipData.KNMAKEJFGDK[stage.Id] = new HGNOJKGKMNH.Types.OCKOAIFFBNG
                        {
                            KNFOFFBOCML = rec.Cleared, MPNGHAKGLDM = ThemeMiniGameStages.Unlocked(tables, state, stage), CGFMBCCDFPG = rec.Best,
                            LELAOAJLKKI = rec.Extra, KNAHJGMNKIL = rec.Stars,
                        };
                    }
                    break;
                case ThemeModules.Audition:
                    reply.AuditionData = new MDJKKAHBGFL();
                    foreach (var stage in stages)
                    {
                        var rec = state.Stages.GetValueOrDefault(stage.Id) ?? new();
                        reply.AuditionData.KNMAKEJFGDK[stage.Id] = new MDJKKAHBGFL.Types.HLADEFFHAMK
                        {
                            KNFOFFBOCML = rec.Cleared, MPNGHAKGLDM = ThemeMiniGameStages.Unlocked(tables, state, stage), CGFMBCCDFPG = rec.Best,
                            Duration = rec.Duration, KNAHJGMNKIL = rec.Stars, NHALLNKPCKG = rec.Extra, IKCHBONJGGL = (MDJKKAHBGFL.Types.OAPMPMKONID)(int)rec.Rank,
                        };
                    }
                    break;
                case ThemeModules.Motorcycle:
                    reply.MotorcycleData = new DNEBAGLMFDO { IJKGNEEBFKJ = state.Points };
                    foreach (var stage in stages)
                    {
                        var rec = state.Stages.GetValueOrDefault(stage.Id) ?? new();
                        reply.MotorcycleData.KNMAKEJFGDK[stage.Id] = new DNEBAGLMFDO.Types.NMCAEALPPKM
                        {
                            KNFOFFBOCML = rec.Cleared, MPNGHAKGLDM = ThemeMiniGameStages.Unlocked(tables, state, stage), CGFMBCCDFPG = rec.Best,
                            Duration = rec.Duration, FOLGGCEMODL = rec.Extra,
                        };
                    }
                    // skill record (client MCPNLANNGMF): field 1 unlocked (its MotorcycleUnlockData row is met), field 2 learned
                    foreach (var skill in ThemeMiniGameStages.MotorcycleSkills(tables, theme))
                        reply.MotorcycleData.EIKDKGLJPKA[skill.Id] = new DNEBAGLMFDO.Types.KOCJGGMOGOK
                        {
                            MPNGHAKGLDM = ThemeMiniGameStages.UnlockMet(tables, state, skill.Unlock), JMALALNMMHC = state.Skills.Contains(skill.Id),
                        };
                    reply.MotorcycleData.JHONJAGOIHM.AddRange(ThemeMiniGameStages.MotorcycleRiders(tables, state, theme));
                    break;
            }
            return reply;
        }

        private void StageStart(CS_ThemeMiniGameStart request, Connection connection)
        {
            uint uid = connection.Account!.Uid;
            lock (AccountLock.For(uid))
            {
                string key = ThemeMiniGameStages.Key(request.ThemeId, request.FNEFKKLJMDN);
                ThemeMiniGameStages.State state = accountState.Get<ThemeMiniGameStages.State>(uid, key) ?? new();
                var stage = ThemeMiniGameStages.Stages(tables, request.ThemeId, request.FNEFKKLJMDN).FirstOrDefault(s => s.Id == request.StageId);
                if (stage is null)
                {
                    connection.Refuse(MsgId.MsgCsThemeMiniGameStart, HNINDAOJCKP.MiniGameStageNotOpen);
                    return;
                }
                if (!ThemeMiniGameStages.Unlocked(tables, state, stage))
                {
                    connection.Refuse(MsgId.MsgCsThemeMiniGameStart, HNINDAOJCKP.MiniGameStageIsLocked);
                    return;
                }

                state.Playing = stage.Id;
                accountState.Set(uid, key, state);
                connection.Send(new SC_ThemeMiniGameStart { ThemeId = request.ThemeId, FNEFKKLJMDN = request.FNEFKKLJMDN, StageId = request.StageId });
            }
        }

        private void StageFinish(CS_ThemeMiniGameFinish request, Connection connection)
        {
            uint uid = connection.Account!.Uid;
            uint module = request.FNEFKKLJMDN;
            lock (AccountLock.For(uid))
            {
                string key = ThemeMiniGameStages.Key(request.ThemeId, module);
                ThemeMiniGameStages.State state = accountState.Get<ThemeMiniGameStages.State>(uid, key) ?? new();
                IReadOnlyList<ThemeMiniGameStages.Stage> stages = ThemeMiniGameStages.Stages(tables, request.ThemeId, module);
                var stage = stages.FirstOrDefault(s => s.Id == request.StageId);
                if (stage is null || state.Playing != stage.Id)
                {
                    connection.Refuse(MsgId.MsgCsThemeMiniGameFinish, HNINDAOJCKP.MiniGameStageIsNotPlaying);
                    return;
                }

                HashSet<uint> openBefore = stages.Where(s => ThemeMiniGameStages.Unlocked(tables, state, s)).Select(s => s.Id).ToHashSet();
                var skills = module == ThemeModules.Motorcycle ? ThemeMiniGameStages.MotorcycleSkills(tables, request.ThemeId) : [];
                HashSet<uint> skillsBefore = skills.Where(s => ThemeMiniGameStages.UnlockMet(tables, state, s.Unlock)).Select(s => s.Id).ToHashSet();

                // Win of the run: card flip and motorcycle report it (LLKGHFKHHHN 1 = win). ASSUMPTION: candy catch and audition report no
                // result, so every finished run counts as cleared.
                bool win = module switch
                {
                    ThemeModules.CardFlip => request.CardFlip?.Result == 1,
                    ThemeModules.Motorcycle => request.Motorcycle?.Result == 1,
                    _ => true,
                };

                var rec = state.Stages.GetValueOrDefault(stage.Id) ?? new ThemeMiniGameStages.StageRecord();
                rec.Best = Math.Max(rec.Best, request.Score);
                if (request.Duration > 0)
                    rec.Duration = rec.Duration == 0 ? request.Duration : Math.Min(rec.Duration, request.Duration);

                // Stars: a star row without condition is the clear itself. ASSUMPTION: candy catch column 4 is the score to reach (it grows
                // 1000/2400 inside a stage); card flip column 4 is the most flips allowed (GJEIFFHNBOB, it shrinks 21/15 inside a stage).
                List<uint> earned = [];
                foreach (var star in ThemeMiniGameStages.StarRows(tables, module, stage))
                {
                    bool got = win && (star.Condition == 0 || module switch
                    {
                        ThemeModules.CandyCatch => request.Score >= star.Condition,
                        ThemeModules.CardFlip => request.CardFlip is not null && request.CardFlip.GJEIFFHNBOB <= star.Condition,
                        _ => false,
                    });
                    if (got) earned.Add(star.Id);
                }

                List<Google.Protobuf.IMessage> messages = [];
                using var drops = RewardDrops.Begin();
                Dictionary<uint, uint> granted = [];
                foreach (var star in ThemeMiniGameStages.StarRows(tables, module, stage).Where(s => earned.Contains(s.Id) && !state.ClaimedStars.Contains(s.Id)))
                {
                    state.ClaimedStars.Add(star.Id);
                    foreach ((uint item, uint count) in star.Reward.Where(r => r.Key != 0 && r.Value > 0))
                    {
                        rewards.Grant(uid, item, (int)Math.Min(count, int.MaxValue), messages);
                        granted[item] = granted.GetValueOrDefault(item) + count;
                    }
                }

                if (win)
                    rec.Cleared = true;
                rec.Stars = Math.Max(rec.Stars, (uint)earned.Count);
                if (module == ThemeModules.CardFlip && win && request.CardFlip is not null)
                    rec.Extra = rec.Extra == 0 ? request.CardFlip.GJEIFFHNBOB : Math.Min(rec.Extra, request.CardFlip.GJEIFFHNBOB);
                if (module == ThemeModules.Audition && request.Audition is not null)
                {
                    rec.Extra = Math.Max(rec.Extra, request.Audition.NHALLNKPCKG);
                    rec.Rank = Math.Max(rec.Rank, (uint)request.Audition.IKCHBONJGGL);
                }
                if (module == ThemeModules.Motorcycle && request.Motorcycle is not null)
                {
                    rec.Extra = Math.Max(rec.Extra, request.Motorcycle.FOLGGCEMODL);
                    // ASSUMPTION: the skill points (IJKGNEEBFKJ, spent by GetSkill against MotorcycleSkillData column 10) are the coins
                    // (FOLGGCEMODL) the run reports; no table names their source.
                    state.Points = (uint)Math.Min((ulong)state.Points + request.Motorcycle.FOLGGCEMODL, uint.MaxValue);
                }
                state.Stages[stage.Id] = rec;
                state.Playing = 0;
                accountState.Set(uid, key, state);

                SC_ThemeMiniGameFinish reply = new()
                {
                    ThemeId = request.ThemeId,
                    FNEFKKLJMDN = module,
                    StageId = request.StageId,
                    Score = request.Score,
                    CGFMBCCDFPG = rec.Best,
                };
                reply.UnlockStage.AddRange(stages.Where(s => !openBefore.Contains(s.Id) && ThemeMiniGameStages.Unlocked(tables, state, s)).Select(s => s.Id));
                switch (module)
                {
                    case ThemeModules.CandyCatch:
                        reply.CandyCatch = new SC_ThemeMiniGameFinish.Types.NCFKOINMGGF { KNAHJGMNKIL = (uint)earned.Count };
                        reply.CandyCatch.Rewards.Add(granted);
                        break;
                    case ThemeModules.CardFlip:
                        reply.CardFlip = new SC_ThemeMiniGameFinish.Types.GOGEBCIACMG
                        {
                            GJEIFFHNBOB = request.CardFlip?.GJEIFFHNBOB ?? 0, LELAOAJLKKI = rec.Extra, KNAHJGMNKIL = (uint)earned.Count,
                        };
                        reply.CardFlip.Rewards.Add(granted);
                        break;
                    case ThemeModules.Audition:
                        reply.Audition = new SC_ThemeMiniGameFinish.Types.AEBFCHHCKAI
                        {
                            HCIKKINNGLD = request.Audition?.HCIKKINNGLD ?? 0, KNAHJGMNKIL = (uint)earned.Count, NHALLNKPCKG = request.Audition?.NHALLNKPCKG ?? 0,
                            EFDLGMODAPK = request.Audition?.IKCHBONJGGL ?? MDJKKAHBGFL.Types.OAPMPMKONID.None,
                            IKCHBONJGGL = (MDJKKAHBGFL.Types.OAPMPMKONID)(int)rec.Rank,
                        };
                        break;
                    case ThemeModules.Motorcycle:
                        reply.Motorcycle = new SC_ThemeMiniGameFinish.Types.DarkZoneRoom
                        {
                            Duration = request.Duration, FOLGGCEMODL = request.Motorcycle?.FOLGGCEMODL ?? 0, IJKGNEEBFKJ = state.Points,
                        };
                        // skills whose unlock row became true with this clear (client sets their field 1)
                        reply.Motorcycle.NLELHHNMBMA.AddRange(skills.Where(s => !skillsBefore.Contains(s.Id) && ThemeMiniGameStages.UnlockMet(tables, state, s.Unlock)).Select(s => s.Id));
                        break;
                }
                drops.Finish(messages);
                messages.Add(reply);
                connection.SendAutoEncrypted(messages.ToArray());
                // the motorcycle score tasks (突破奖励) follow the new best score at once (A-226)
                if (module == ThemeModules.Motorcycle && eventTasks.OnMotorcycleRun(connection.Account!.Uid) is { Count: > 0 } taskUpdates)
                    connection.SendAutoEncrypted(taskUpdates.ToArray());
            }
        }

        private Dictionary<string, ThemeMiniGameState> GetStates(uint uid) =>
            accountState.Get<Dictionary<string, ThemeMiniGameState>>(uid, StateKey) ?? [];

        private static string Key(uint themeId, uint gameId) => $"{themeId}:{gameId}";

        private static ThemeMiniGameState GetState(Dictionary<string, ThemeMiniGameState> states, uint themeId, uint gameId)
        {
            string key = Key(themeId, gameId);
            if (states.TryGetValue(key, out ThemeMiniGameState? state))
                return state;

            state = new ThemeMiniGameState(themeId, gameId, 0, 0, 0, false, 0, 0);
            states[key] = state;
            return state;
        }

        private sealed record ThemeMiniGameState(
            uint ThemeId,
            uint GameId,
            uint BestScore,
            uint Duration,
            uint Times,
            bool Unlocked,
            uint StageId,
            long StartedAtUnixSeconds);
    }
}
