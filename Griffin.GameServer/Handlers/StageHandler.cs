using Griffin.Common.Networking;
using Griffin.Common.Protocol;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Google.Protobuf;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    public sealed class StageHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IGunService gunService,
        IWeaponService weaponService,
        IWeaponModService weaponModService,
        ILobbyCookingService cooking,
        IResourceService resourceService,
        IStaminaService staminaService,
        IDailyQuestProgressService questProgress,
        StageResultHandler results) : StageHandlerBase
    {
        public override void HandleStageUsableGuns(CS_StageUsableGuns request, Connection connection)
        {
            // lineup / lineup_guns are the characters a stage LENDS to the player (trial or fixed squads); for an ordinary stage the
            // client uses the account's own characters and the official reply is empty. Filling lineup_guns with every owned
            // character (what this handler did until 13:00) made the client's formation screen mix them with its own list: the
            // 12:56 session ended in a native stack overflow (0xC00000FD, a 5-function cycle repeated ~290 times) while it loaded
            // the third character model of the 1-4 squad. The original server did not answer this request either.
            var response = new SC_StageUsableGuns { StageId = request.StageId };
            // The deployment screen preselects `lineup` (field 2, one gun id per slot, 0 = empty) and orders the turns by `sequence` (field 4): the team the
            // player took into their last battle, remembered from CS_StageInto (A-90). Characters the account no longer owns are left out.
            if (connection.Account is not null && accountState.Get<SavedLineup>(connection.Account.Uid, "stage.lineup") is { } saved)
            {
                HashSet<uint> owned = gunService.GetPlayerGuns(connection.Account.Uid).Select(g => g.GunId).ToHashSet();
                response.Lineup.AddRange(saved.Lineup.Select(id => owned.Contains(id) ? id : 0u));
                response.Sequence.AddRange(saved.Sequence.Where(owned.Contains));
            }

            connection.Send(response);
        }

        public override void HandleStageRecord(CS_StageRecord request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var records = new Dictionary<uint, StageRecord>();
            var stored = accountState.Get<Dictionary<string, StageRecord>>(connection.Account.Uid, "stage.records") ?? [];
            var metChallenges = accountState.Get<Dictionary<string, HashSet<uint>>>(connection.Account.Uid, "stage.challenge_ids") ?? [];
            foreach (StageData stage in tableService.GetTable<StageData>())
            {
                if (stored.TryGetValue(stage.Id.ToString(), out StageRecord? record))
                {
                    // complete_challenges is a bitmask over StageData.challenge_list; records saved before 21:50 held a COUNT there, so
                    // the mask is rebuilt from the objectives actually reported (none known = no objective marked).
                    record = record.Clone();
                    record.CompleteChallenges = StageResultHandler.ChallengeMask(stage, metChallenges.GetValueOrDefault(stage.Id.ToString()) ?? []);
                    records[stage.Id] = record;
                }
            }

            // The opening stages are always reported as passed, exactly as the original server's records did (types 11, 12, 13): the
            // 4.0.5136 client keeps its records per stage type and, with 1-1..1-3 not passed, starts the 1-1 tutorial battle by
            // itself right after the lobby loads, then 1-4 and so on (12:32 and 12:53 sessions). The forced tutorial battles need
            // the full unit data of the official server, which is not complete yet.
            foreach ((uint stageId, long passedAt, uint challenges) in ClearedStages)
            {
                if (!records.ContainsKey(stageId) && tableService.GetTable<StageData>().Any(stage => stage.Id == stageId))
                {
                    records[stageId] = new StageRecord
                    {
                        StageId = stageId,
                        FirstPassTime = passedAt,
                        CompleteChallenges = challenges,
                        HKEGGELDDEP = 1,
                        MCDNPPCAFOK = true,
                    };
                }
            }

            connection.Send(new SC_StageRecord { Records = { records } });
        }

        /// <summary>Stage, first pass time and cleared challenges of the original server's records (the prologue and the early campaign).</summary>
        internal static readonly (uint StageId, long PassedAt, uint Challenges)[] ClearedStages =
        [
            (10101, 1703900194, 0), (10102, 1703900349, 0), (10103, 1703900704, 7),
            (10104, 1703900906, 7), (10105, 1703901048, 7), (10106, 1703901190, 7), (10107, 1728667765, 7),
            (10108, 1728668071, 7), (10109, 1728668217, 7), (10110, 1728668611, 7),
            (10141, 1728667617, 7), (10142, 1728667936, 7), (10143, 1728668327, 7),
            (10183, 1703901077, 0), (10184, 1703901217, 0),
            (10001, 1703592371, 0),
        ];

        public override void HandleStageStart(CS_StageStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            StageData? stage = tableService.GetTable<StageData>().FirstOrDefault(row => row.Id == request.StcStageId);
            if (stage is null)
            {
                Log.Warning("Account {Uid} requested unknown stage {StageId}.", connection.Account.Uid, request.StcStageId);
                connection.Send(new SC_StageStart());
                return;
            }

            // StageData fields 8/9 are cost item and amount in the installed 4.0.5136 tables.
            // Charge before opening the stage so the client cannot start a paid run without stamina.
            uint costItem = TableFields.U(stage, 8);
            uint costAmount = TableFields.U(stage, 9);
            // Test switch of the campaign sweep: thousands of stages in one run cannot be paid with the stamina of one account.
            if (Environment.GetEnvironmentVariable("GRIFFIN_FREE_STAGES") == "1")
                costItem = costAmount = 0;
            SC_ResourceUpdate? resourceUpdate = null;
            if (costItem != 0 && costAmount != 0)
            {
                bool spent;
                if (costItem == 101)
                {
                    // Stamina is Resource.stamina_type_resource, not an entry in resource_ex / ItemEntity.
                    // The startup response currently seeds 183 for item 101; LoginHandler must seed/read this
                    // persisted value too so reconnects retain the balance.
                    if (costAmount > int.MaxValue)
                    {
                        spent = false;
                    }
                    else
                    {
                        spent = staminaService.TrySpend(connection.Account.Uid, (int)costAmount,
                            startingValue: StaminaService.StartingValue, refreshTime: connection.GameClockSeconds(), out StaminaBalance balance);
                        if (spent)
                            connection.SendRawPacket((ushort)MsgId.MsgScStaminaTypeResourceUpdate,
                                StaminaService.BuildUpdatePayload(itemId: 101, balance));
                    }
                }
                else
                {
                    spent = costAmount <= int.MaxValue && resourceService.TrySpend(connection.Account.Uid,
                        new Dictionary<uint, int> { [costItem] = (int)costAmount }, out resourceUpdate);
                }

                if (!spent)
                {
                    connection.Refuse(MsgId.MsgCsStageStart,
                        costItem == 101 ? HNINDAOJCKP.StaminaNotEnough : HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                // DailyQuestData condition class 2/link 1000 counts Intelligence Puzzle (stamina) spent.
                if (costItem == 101)
                    questProgress.AddCondition(connection.Account.Uid, conditionClass: 2, link: "1000", amount: costAmount);
            }

            StageBattle battle = CreateBattle(stage, request.IsAuto);
            accountState.Set(connection.Account.Uid, "stage.pending",
                new PendingStage(stage.Id, request.IsAuto, battle.Id, battle.RandomSeed, request.Other?.Together?.GunId ?? 0, request.Other?.Together?.KFIAKLNJHMB ?? 0));
            if (resourceUpdate is not null)
                connection.SendAutoEncrypted(resourceUpdate);
            // a plot-only stage (dialogue, no battle) is passed by playing it: the client plays the AVG by itself and reports nothing at the end (A-63).
            // It gets no SC_StageStart: that reply is the "battle starts" one and the client's handler threw an error with it after the dialogue (A-66).
            bool plotOnly = TableFields.U(stage, 21) == 3;
            if (!plotOnly)
                connection.Send(new SC_StageStart
                {
                    Stage = battle,
                    Other = request.Other?.Clone() ?? new StageOther(),
                });
            Log.Information("Stage {StageId} started for account {Uid} (auto={Auto}).", stage.Id, connection.Account.Uid, request.IsAuto);

            if (plotOnly)
                results.CompletePlotStage(connection, connection.Account.Uid);
        }

        public override void HandleStageInto(CS_StageInto request, Connection connection)
        {
            if (connection.Account is null)
                return;

            PendingStage? pending = accountState.Get<PendingStage>(connection.Account.Uid, "stage.pending");
            StageData? stage = pending is null ? null : tableService.GetTable<StageData>().FirstOrDefault(row => row.Id == pending.StageId);
            if (stage is null)
            {
                Log.Warning("Account {Uid} requested stage entry without a valid pending stage.", connection.Account.Uid);
                connection.Send(new SC_StageInto());
                return;
            }

            accountState.Set(connection.Account.Uid, "stage.lineup", new SavedLineup(request.Guns.Values.ToList(), request.ActionSequence.ToList()));
            StageBattle battle = CreateBattle(stage, pending!.IsAuto, request.Guns, connection,
                pending.BattleId, pending.RandomSeed);
            if (pending.BattleId == 0 || pending.RandomSeed == 0)
            {
                // Migrate an in-flight battle created by an older server build without
                // replacing its stage/auto selection.
                accountState.Set(connection.Account.Uid, "stage.pending",
                    pending with { BattleId = battle.Id, RandomSeed = battle.RandomSeed });
            }

            connection.Send(new SC_StageInto
            {
                Stage = battle,
                Other = pending.TogetherGun == 0 ? new StageOther() : new StageOther { Together = new CKAOJMGCFFF { GunId = pending.TogetherGun, KFIAKLNJHMB = pending.TogetherLevel } },
            });
        }

        public override void HandleStageExit(CS_StageExit request, Connection connection)
        {
            if (connection.Account is null)
                return;

            accountState.Set<PendingStage?>(connection.Account.Uid, "stage.pending", null);
            connection.Send(new SC_StageExit { State = request.State });
        }

        private StageBattle CreateBattle(StageData stage, bool isAuto,
            Google.Protobuf.Collections.MapField<uint, uint>? requestedGuns = null,
            Connection? connection = null, long battleId = 0, long randomSeed = 0)
        {
            uint battleLevel = Math.Max(1u, stage.RecommandedPlayerlevel);
            var battle = new StageBattle
            {
                Id = battleId > 0 ? battleId : Random.Shared.NextInt64(1, long.MaxValue),
                StcStageId = stage.Id,
                StageClass = (JLHABLIBDCH)stage.StageClass,
                RandomSeed = randomSeed > 0 ? randomSeed : Random.Shared.NextInt64(1, long.MaxValue),
                BattleLevel = battleLevel,
                Auto = isAuto,
                Side = BattleUnitBuilder.PlayerSide,
                FormationId = 0,
            };

            if (connection?.Account is not { } account)
                return battle;

            ulong uid = account.Uid;
            battle.UserSides[uid] = BattleUnitBuilder.PlayerSide;
            StageConfigData? config = tableService.GetTable<StageConfigData>()
                .FirstOrDefault(row => row.StageId == stage.StageConfig);
            if (config is null)
            {
                Log.Warning("Stage {StageId} has no StageConfigData row {ConfigId}.", stage.Id, stage.StageConfig);
                return battle;
            }

            Dictionary<uint, GunEntity> ownedGuns = gunService.GetPlayerGuns(account.Uid)
                .GroupBy(gun => gun.GunId).ToDictionary(group => group.Key, group => group.First());
            WeaponEntity[] weapons = weaponService.GetPlayerWeapons(account.Uid);
            WeaponEntity? WeaponOf(GunEntity gun) =>
                weapons.FirstOrDefault(w => w.Id == gun.WeaponId && w.Id != 0) ?? weapons.FirstOrDefault(w => w.GunId == gun.GunId);
            // "pointId:(rotation):gunId:..." per starter unit: the facing the client expects for each deployment point
            Dictionary<uint, string> rotations = config.DefaultGuns.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(row => row.Split(':'))
                .Where(fields => fields.Length >= 2 && uint.TryParse(fields[0], out _))
                .GroupBy(fields => uint.Parse(fields[0]))
                .ToDictionary(group => group.Key, group => group.First()[1]);
            string RotationOf(uint pointId) => rotations.GetValueOrDefault(pointId, BattleUnitBuilder.DefaultRotation);
            var playerUnits = new List<byte[]>();
            // account data that adds to the stats of every character: control room level and the stages passed (A-73)
            uint controlRoomLevel = accountState.Get<ControlRoomHandler.RoomState>(account.Uid, ControlRoomHandler.StateKey)?.Level ?? 1;
            HashSet<uint> passedStages = (accountState.Get<Dictionary<string, StageRecord>>(account.Uid, "stage.records") ?? [])
                .Where(entry => entry.Value.FirstPassTime != 0 && uint.TryParse(entry.Key, out _)).Select(entry => uint.Parse(entry.Key)).ToHashSet();
            WeaponModEntity[] mods = weaponModService.GetPlayerWeaponMods(account.Uid);
            Dictionary<string, ModRefineState> allRefine = ModPolarity.All(accountState, account.Uid);
            IReadOnlyList<uint> supportRows = SupportChips.ActiveRows(tableService, accountState, account.Uid);
            IReadOnlyList<(ulong Id, uint StcId)> publicKeys = PublicTalentHandler.ItemsOf(accountState, account.Uid);
            StatInputs InputsOf(GunEntity gun) => new(gun, controlRoomLevel, passedStages,
                (WeaponOf(gun)?.EquippedModIds ?? []).Where(id => id != 0).Select(id => mods.FirstOrDefault(m => m.Id == id)).Where(m => m is not null)
                    .SelectMany(m => ModPolarity.Values(m!.Field7, ModPolarity.From(allRefine, m.Id).Polarity)).ToList(),
                gun.PublicTalentSkillItemsUid.Where(id => id != 0).Select(id => publicKeys.FirstOrDefault(k => k.Id == id)).Where(k => k.Id != 0)
                    .Select(k => (k.StcId, PublicTalentHandler.AffixesOf(accountState, account.Uid, k.Id))).ToList(),
                PluginAffixesOf(account.Uid, gun.GunId), cooking.FoodRows(account.Uid, gun.GunId), supportRows,
                ModSkillsOf(WeaponOf(gun)?.EquippedModIds ?? [], mods));
            if (requestedGuns is { Count: > 0 })
            {
                foreach ((uint unitId, uint gunId) in requestedGuns.OrderBy(pair => pair.Key))
                {
                    if (!ownedGuns.TryGetValue(gunId, out GunEntity? gun))
                        continue;
                    playerUnits.Add(BattleUnitBuilder.GunUnit(tableService, unitId, gun.GunId, gun.ToProtoGunCharacter(), WeaponOf(gun), RotationOf(unitId), InputsOf(gun)));
                }
            }

            // The first tutorial may omit the selection map. Its own table defines the starter
            // deployment; include only characters actually present in this account.
            if (playerUnits.Count == 0)
            {
                foreach (string row in config.DefaultGuns.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = row.Split(':');
                    if (fields.Length < 3
                        || !uint.TryParse(fields[0], out uint unitId)
                        || !uint.TryParse(fields[2], out uint gunId)
                        || !ownedGuns.TryGetValue(gunId, out GunEntity? gun))
                        continue;
                    playerUnits.Add(BattleUnitBuilder.GunUnit(tableService, unitId, gun.GunId, gun.ToProtoGunCharacter(), WeaponOf(gun), RotationOf(unitId), InputsOf(gun)));
                }
            }

            // The 4.0.5136 client's own StageBattle constructor initializes two camps (player side 1 and enemy side 2), then creates
            // stage enemies from its own StageConfig/StageEnemy tables. Mirror both descriptors. The player's missing action flow was
            // observed with the prior one-camp response; the empty second descriptor is a protocol-shape correction to verify in-client.
            // Keep it empty to avoid duplicating the enemies the client creates locally.
            battle.Camps.Add(BattleUnitBuilder.Camp(BattleUnitBuilder.PlayerSide, playerUnits));
            battle.Camps.Add(BattleUnitBuilder.Camp(BattleUnitBuilder.EnemySide, []));

            Log.Information("Stage {StageId} battle composed from table config {ConfigId}: {PlayerUnits} player units.",
                stage.Id, config.StageId, playerUnits.Count);
            return battle;
        }

        /// <summary>The powers of the mods a weapon carries (A-78): one skill each with the numbers of its rank; two mods with the same power give the larger numbers once.</summary>
        private IReadOnlyList<(uint Skill, int[] Values)> ModSkillsOf(IEnumerable<uint> equippedIds, WeaponModEntity[] mods)
        {
            var skills = new Dictionary<uint, int[]>();
            foreach (uint id in equippedIds.Where(id => id != 0))
                if (mods.FirstOrDefault(m => m.Id == id) is { } mod && ModPower.SkillOf(tableService, mod) is { } power)
                    if (!skills.TryGetValue(power.Skill, out int[]? known) || known.Sum() < power.Values.Sum())
                        skills[power.Skill] = power.Values;

            return skills.Select(pair => (pair.Key, pair.Value)).ToList();
        }

        /// <summary>The affixes of the plugins in the six slots of the core of a character (A-75).</summary>
        private IReadOnlyList<uint> PluginAffixesOf(uint uid, uint gunId)
        {
            ulong[] slots = RemouldingPlugins.SlotsOf(accountState, uid, gunId);
            return GunHandler.PluginCatalog().RemouldingPlugins.Where(plugin => slots.Contains(plugin.Id)).SelectMany(plugin => plugin.ACEIFDEGLMH).ToList();
        }

        /// <summary>TogetherGun/TogetherLevel: the character of a together-battle (affection event, A-55) that the client names in <c>StageOther.together</c>.</summary>
        internal sealed record PendingStage(uint StageId, bool IsAuto, long BattleId = 0, long RandomSeed = 0, uint TogetherGun = 0, uint TogetherLevel = 0);
        internal sealed record SavedLineup(List<uint> Lineup, List<uint> Sequence);


        // Borrowed (friend/preset) assistants of a stage: the account has no friends, so none are offered.
        public override void HandleStageAssistantRandom(CS_StageAssistantRandom request, Connection connection) =>
            connection.Send(new SC_StageAssistantRandom { StageId = request.StageId });

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> StageOpSeq = new();

        /// <summary>Relay of a battle operation: the client simulates locally, so the operation is returned with the next sequence number.</summary>
        public override void HandleStageOp(CS_StageOp request, Connection connection)
        {
            uint seq = connection.Account is null ? 1 : StageOpSeq.AddOrUpdate(connection.Account.Uid, 1, (_, s) => s + 1);
            connection.Send(new SC_StageOp { Seq = seq, Op = request.Op, Timestamp = request.Timestamp });
        }
    }
}
