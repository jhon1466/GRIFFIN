using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Rouge2 (RougeGame, plan 600004), the second roguelike mode. There was only an empty Info. A run goes through the opening phases
    /// (IFKECOJMCHO.OKHIGJOBCNM: difficulty → sect → doll → chip → precious → confirm → started), then a map of layers (HHLHLIMEJBE) whose
    /// nodes (BEIJDBCIDNC) are fights from Rouge2BattlePoolData (3 = stage); a won fight offers a choice (LIPIBCHIEDC: dolls, chips or
    /// precious) and the last node of a layer opens the next one.
    /// ASSUMPTIONS A-196: the offers are three random rows of Rouge2GunData (2 = doll), Rouge2ChipData and Rouge2PreciousData; a layer has
    /// three fight nodes and the run has <see cref="Layers"/> layers; node field 2 = id, 3 = kind (1 fight), 5 = done, 6 = current.
    /// </summary>
    public interface IRouge2Service : IGameService
    {
        CCIBDHDDGCD? Run(uint uid, uint plan);

        SC_RougeGameOpening Opening(uint uid, CS_RougeGameOpening request);

        SC_RougeGameNode Node(uint uid, CS_RougeGameNode request);

        SC_RougeGameOption Option(uint uid, CS_RougeGameOption request);

        SC_RougeGameNextLayer NextLayer(uint uid, CS_RougeGameNextLayer request);

        SC_RougeGameMapLayer MapLayer(uint uid, CS_RougeGameMapLayer request);

        void OnStageResult(uint uid, uint stageId, bool won, List<IMessage> push);
    }

    public sealed class Rouge2Service(ITableService tableService, IAccountStateService accountState) : IRouge2Service
    {
        private const int Layers = 5;
        private const int NodesPerLayer = 3;

        public sealed class MapNode
        {
            public uint Id { get; set; }
            public uint Battle { get; set; }
            public uint Stage { get; set; }
            public bool Done { get; set; }
        }

        public sealed class State
        {
            public uint Plan { get; set; }
            public int Phase { get; set; } = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseDifficulty;
            public uint Difficulty { get; set; }
            public List<uint> DifficultyArgs { get; set; } = [];
            public uint Sect { get; set; }
            public List<uint> Guns { get; set; } = [];
            public List<uint> Chips { get; set; } = [];
            public List<uint> Precious { get; set; } = [];
            public string OfferKind { get; set; } = string.Empty;
            public List<uint> Offer { get; set; } = [];
            public uint Layer { get; set; }
            public List<List<MapNode>> Map { get; set; } = [];
            public uint CurrentNode { get; set; }
            public long StartTime { get; set; }
        }

        private static string Key(uint plan) => $"rouge2.{plan}";

        private State? Load(uint uid, uint plan) => accountState.Get<State>(uid, Key(plan));

        private void Save(uint uid, State state) => accountState.Set(uid, Key(state.Plan), state);

        private List<uint> Pick(IEnumerable<uint> pool, int count, IEnumerable<uint> exclude)
        {
            HashSet<uint> skip = exclude.ToHashSet();
            List<uint> candidates = pool.Where(id => id != 0 && !skip.Contains(id)).Distinct().ToList();
            return candidates.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();
        }

        private void Offer(State state, string kind)
        {
            state.OfferKind = kind;
            state.Offer = kind switch
            {
                "gun" => Pick(tableService.GetTable<Rouge2GunData>().Select(r => TableFields.U(r, 2)), 3, state.Guns),
                "chip" => Pick(tableService.GetTable<Rouge2ChipData>().Select(r => TableFields.U(r, 1)), 3, state.Chips),
                "precious" => Pick(tableService.GetTable<Rouge2PreciousData>().Select(r => TableFields.U(r, 1)), 3, state.Precious),
                _ => [],
            };
        }

        private LIPIBCHIEDC OfferProto(State state)
        {
            LIPIBCHIEDC option = new();
            switch (state.OfferKind)
            {
                case "gun":
                    LIPIBCHIEDC.Types.GENCFMKMIMN guns = new();
                    guns.NDKGOAHEAMJ.AddRange(state.Offer.Select(id => new StageEnemy { GunId = id }));
                    option.Guns = guns;
                    break;
                case "chip":
                    LIPIBCHIEDC.Types.NMDHLNDFMLH chips = new();
                    chips.KHCOBICFFMB.AddRange(state.Offer);
                    option.Chips = chips;
                    break;
                case "precious":
                    LIPIBCHIEDC.Types.MonsterInfo precious = new();
                    precious.DCOABIJHPBO.AddRange(state.Offer);
                    option.Precious = precious;
                    break;
            }

            return option;
        }

        private IFKECOJMCHO OpeningProto(State state)
        {
            IFKECOJMCHO opening = new() { IFNEDNHKBJN = state.Difficulty, IJPNIFKPALL = state.Sect, KFJDLALJNDI = (IFKECOJMCHO.Types.OKHIGJOBCNM)state.Phase };
            opening.JMHJALPBGLA.AddRange(state.DifficultyArgs);
            opening.GGOLBAILKIO.AddRange(state.Guns);
            opening.AIDCCKPDEFL.AddRange(state.Chips);
            opening.DJGHDKIDMKO.AddRange(state.Precious);
            return opening;
        }

        private HHLHLIMEJBE LayerProto(State state, uint layer)
        {
            HHLHLIMEJBE proto = new() { KCGAILCKCLC = layer };
            if (layer >= 1 && layer <= state.Map.Count)
                foreach (MapNode node in state.Map[(int)layer - 1])
                    proto.LGNOAHMHJHH.Add(NodeProto(state, node));
            return proto;
        }

        private BEIJDBCIDNC NodeProto(State state, MapNode node) => new()
        {
            JBEONCNOEEA = node.Id,
            AIODFHGMBME = 1,
            ANJHKELIAFF = node.Battle,
            JONLCAGBGJE = node.Done,
            PMEDDDCJKNE = node.Id == state.CurrentNode,
            BattleInfo = new LIPIBCHIEDC.Types.IKOCIJGHLPL { BattleId = node.Battle, Win = node.Done },
        };

        private void BuildLayer(State state)
        {
            List<Rouge2BattlePoolData> pool = tableService.GetTable<Rouge2BattlePoolData>().ToList();
            List<MapNode> nodes = [];
            for (int i = 0; i < NodesPerLayer && pool.Count > 0; i++)
            {
                Rouge2BattlePoolData battle = pool[Random.Shared.Next(pool.Count)];
                nodes.Add(new MapNode { Id = state.Layer * 100 + (uint)i + 1, Battle = TableFields.U(battle, 1), Stage = TableFields.U(battle, 3) });
            }

            state.Map.Add(nodes);
        }

        public CCIBDHDDGCD? Run(uint uid, uint plan)
        {
            State? state = Load(uid, plan);
            if (state is null)
                return null;
            CCIBDHDDGCD run = new() { KOHGNMPNAMJ = OpeningProto(state), EABFOMIKCDM = OfferProto(state), StartTime = state.StartTime };
            run.Guns.AddRange(state.Guns.Select(id => new StageEnemy { GunId = id }));
            if (state.Layer > 0)
            {
                BCMKILHKMEH map = new() { BCCLAJIJJOA = state.Layer, EOGKFFCDMMJ = (uint)state.Map.Count };
                for (uint layer = 1; layer <= state.Map.Count; layer++)
                    map.KDOHJCDHBLF.Add(LayerProto(state, layer));
                run.Map = map;
            }

            return run;
        }

        public SC_RougeGameOpening Opening(uint uid, CS_RougeGameOpening request)
        {
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.PlanId) ?? new State { Plan = request.PlanId };
                switch (request.EHBNJHGCLGNCase)
                {
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Difficulty:
                        state.Difficulty = request.Difficulty.IFNEDNHKBJN;
                        state.DifficultyArgs = [.. request.Difficulty.KLJDJPFMKMN];
                        state.Phase = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseSect;
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Sect:
                        state.Sect = request.Sect.IJPNIFKPALL;
                        state.Phase = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseGun;
                        Offer(state, "gun");
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Gun:
                        if (!state.Guns.Contains(request.Gun.GunId))
                            state.Guns.Add(request.Gun.GunId);
                        state.Phase = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseChip;
                        Offer(state, "chip");
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Chip:
                        state.Chips.Add(request.Chip.ODMEICDCEDP);
                        state.Phase = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhasePrecious;
                        Offer(state, "precious");
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Precious:
                        state.Precious.Add(request.Precious.JBGNPDHGCCB);
                        state.Phase = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseConfirmStart;
                        Offer(state, string.Empty);
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Start:
                        state.Phase = (int)IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseStarted;
                        state.StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        state.Layer = 1;
                        state.Map.Clear();
                        BuildLayer(state);
                        state.CurrentNode = state.Map[0].FirstOrDefault()?.Id ?? 0;
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.Reopen:
                        state = new State { Plan = request.PlanId };
                        break;
                    case CS_RougeGameOpening.EHBNJHGCLGNOneofCase.GiveUp:
                        accountState.Set<State?>(uid, Key(request.PlanId), null);
                        return new SC_RougeGameOpening { PlanId = request.PlanId, EAPNGPFEJNM = new IFKECOJMCHO { KFJDLALJNDI = IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseDifficulty } };
                }

                Save(uid, state);
                SC_RougeGameOpening reply = new() { PlanId = request.PlanId, FADFGFNOGEB = OfferProto(state), EAPNGPFEJNM = OpeningProto(state), CBNMNFAOMLL = true };
                reply.Guns.AddRange(state.Guns.Select(id => new StageEnemy { GunId = id }));
                return reply;
            }
        }

        public SC_RougeGameNode Node(uint uid, CS_RougeGameNode request)
        {
            lock (AccountLock.For(uid))
            {
                State? state = Load(uid, request.PlanId);
                MapNode? node = state?.Map.SelectMany(l => l).FirstOrDefault(n => n.Id == request.JBEONCNOEEA);
                if (state is null || node is null)
                    return new SC_RougeGameNode { PlanId = request.PlanId };

                state.CurrentNode = node.Id;
                if (request.OIALKMKLMKICase is CS_RougeGameNode.OIALKMKLMKIOneofCase.BattleSkip or CS_RougeGameNode.OIALKMKLMKIOneofCase.Finish && !node.Done)
                {
                    node.Done = true;
                    Offer(state, (new[] { "gun", "chip", "precious" })[Random.Shared.Next(3)]);
                }

                Save(uid, state);
                return new SC_RougeGameNode { PlanId = request.PlanId, GCMADDJNGMI = NodeProto(state, node), FADFGFNOGEB = OfferProto(state) };
            }
        }

        public SC_RougeGameOption Option(uint uid, CS_RougeGameOption request)
        {
            lock (AccountLock.For(uid))
            {
                State? state = Load(uid, request.PlanId);
                if (state is null)
                    return new SC_RougeGameOption { PlanId = request.PlanId };

                switch (request.EANNMDMFKHDCase)
                {
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.Gun:
                        if (!state.Guns.Contains(request.Gun.GunId))
                            state.Guns.Add(request.Gun.GunId);
                        Offer(state, string.Empty);
                        break;
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.Chip:
                        state.Chips.Add(request.Chip.ODMEICDCEDP);
                        Offer(state, string.Empty);
                        break;
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.Precious:
                        state.Precious.Add(request.Precious.JBGNPDHGCCB);
                        Offer(state, string.Empty);
                        break;
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.RefreshGun:
                        Offer(state, "gun");
                        break;
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.RefreshChip:
                        Offer(state, "chip");
                        break;
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.RefreshPrecious:
                        Offer(state, "precious");
                        break;
                    case CS_RougeGameOption.EANNMDMFKHDOneofCase.GiveUp:
                        Offer(state, string.Empty);
                        break;
                }

                Save(uid, state);
                return new SC_RougeGameOption { PlanId = request.PlanId, FADFGFNOGEB = OfferProto(state) };
            }
        }

        public SC_RougeGameNextLayer NextLayer(uint uid, CS_RougeGameNextLayer request)
        {
            lock (AccountLock.For(uid))
            {
                State? state = Load(uid, request.PlanId);
                if (state is null)
                    return new SC_RougeGameNextLayer { PlanId = request.PlanId };

                if (state.Layer < Layers)
                {
                    state.Layer++;
                    BuildLayer(state);
                    state.CurrentNode = state.Map[^1].FirstOrDefault()?.Id ?? 0;
                }

                Save(uid, state);
                return new SC_RougeGameNextLayer { PlanId = request.PlanId, BLFKCHCCLGI = LayerProto(state, state.Layer) };
            }
        }

        public SC_RougeGameMapLayer MapLayer(uint uid, CS_RougeGameMapLayer request)
        {
            State? state = Load(uid, request.PlanId);
            return new SC_RougeGameMapLayer { PlanId = request.PlanId, BDEGMAFIOFK = state is null ? new HHLHLIMEJBE() : LayerProto(state, request.JBONGGMEDFF == 0 ? state.Layer : request.JBONGGMEDFF) };
        }

        public void OnStageResult(uint uid, uint stageId, bool won, List<IMessage> push)
        {
            foreach (uint plan in new uint[] { 600004 })
            {
                lock (AccountLock.For(uid))
                {
                    State? state = Load(uid, plan);
                    MapNode? node = state?.Map.SelectMany(l => l).FirstOrDefault(n => n.Id == state.CurrentNode && n.Stage == stageId);
                    if (state is null || node is null || !won || node.Done)
                        continue;
                    node.Done = true;
                    Offer(state, (new[] { "gun", "chip", "precious" })[Random.Shared.Next(3)]);
                    Save(uid, state);
                    push.Add(new SC_RougeGameNode { PlanId = plan, GCMADDJNGMI = NodeProto(state, node), FADFGFNOGEB = OfferProto(state) });
                }
            }
        }
    }
}
