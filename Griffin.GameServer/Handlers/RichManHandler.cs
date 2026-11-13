using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Google.Protobuf;
using static Griffin.Common.Proto.KMOMJFOFEDD.Types;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// RichMan board game of the events (module 21003, e.g. theme 901 -> RichmanConfigData 1001). As in the client data manager (client 4.0.5142):
    /// the config row's season (column 4) is a MissionBySeasonData row whose column 2 lists the stages (MissionData: 1 id, 4 type, 2 = endless,
    /// 5 minutes after the PlanData start of the module (ActivityModuleData column 10) before the stage opens, 6 stage that must be passed
    /// first, 7 number of opponents of the endless stage, 8 maps, 11 opponents (GunSpineData), 13 shop). SC_RichManData carries per stage:
    /// stage id, best score, whether a game is being played on it, and a passed counter (field 4; the client opens the next stage when > 0).
    ///
    /// The board is simulated here, as the official server does: the client only plays back the events it is sent (SC_RichManEventNotify,
    /// queued and executed in order by the client event runner) and sends commands (CS_RichManCmd). The board graph is MissionMapData of the
    /// map (MissionMapByMapIdData): column 3 point id, 4 = 1 for a board grid, 5 "next:flag" links (flag 1 = forward), 6 grid type
    /// (2 normal, 3 land, 5 mini game, 6 random, 8 building slot, 9 loot, 10 shop, 11 build, 12/13 transmit), 7 score of a normal grid,
    /// 14 start grid of the n-th player, 15 building slot of a build grid.
    /// A turn of the client: PawnAction(player) opens its command panel (player 0 = the account); Roll -> Roll event, one Move event per
    /// step (Path event at a fork, answered by a Direction command), then the grid of arrival (GridScore, Shop, Build...), PlayerActionEnd;
    /// the opponents play their turn, a Round event starts the next round and PawnAction(0) gives the hand back.
    /// The client ends the game itself (no dice left, or the account / every opponent out) and asks SettleGame.
    /// </summary>
    public sealed class RichManHandler(IAccountStateService accountState, ITableService tableService) : RichManHandlerBase
    {
        public sealed class Record
        {
            public int BestScore { get; set; }
            public int Passed { get; set; }
        }

        public sealed class Player
        {
            public uint Id { get; set; }
            public uint Pos { get; set; }
            public int Score { get; set; }
            public int[] Dice { get; set; } = new int[3];
            public bool Out { get; set; }
            public List<uint> Cards { get; set; } = [];
            public List<uint> Cards2 { get; set; } = [];
        }

        public sealed class Building
        {
            public uint Id { get; set; }
            public int Owner { get; set; } = -1;
        }

        public enum Wait
        {
            Roll,
            Path,
            Shop,
            Build,
        }

        public sealed class Game
        {
            public uint Stage { get; set; }
            public uint Map { get; set; }
            public long RoomId { get; set; }
            public uint Round { get; set; } = 1;
            public List<Player> Players { get; set; } = [];
            public Dictionary<uint, Building> Buildings { get; set; } = [];
            public Wait Wait { get; set; }
            public int StepsLeft { get; set; }
            public uint WaitGrid { get; set; }
            public List<uint> ShopGoods { get; set; } = [];
            public List<uint> ShopBought { get; set; } = [];
            public uint ShopRefresh { get; set; }
            public bool Finished { get; set; }
        }

        private sealed class Grid
        {
            public uint Id;
            public uint Type;
            public int Score;
            public uint Spawn;
            public uint Slot;
            public List<uint> Next = [];
        }

        private const uint GridNormal = 2, GridShop = 10, GridBuild = 11;

        private static readonly Dictionary<uint, Dictionary<uint, Grid>> MapCache = [];

        private static string Key(uint theme) => $"richman.{theme}";

        private static string GameKey(uint theme) => $"richman.{theme}.game";

        private Dictionary<uint, Record> Load(uint uid, uint theme) => accountState.Get<Dictionary<uint, Record>>(uid, Key(theme)) ?? [];

        private Game? LoadGame(uint uid, uint theme) => accountState.Get<Game>(uid, GameKey(theme));

        /// <summary>The MissionData rows of the theme, in the order of MissionBySeasonData.</summary>
        private List<IMessage> Stages(uint theme)
        {
            uint config = ThemeModules.Config(tableService, theme, ThemeModules.RichMan);
            var configRow = tableService.GetTable<RichmanConfigData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
            if (configRow is null)
                return [];
            uint season = TableFields.U(configRow, 4);
            var bySeason = tableService.GetTable<MissionBySeasonData>().FirstOrDefault(r => TableFields.U(r, 1) == season);
            if (bySeason is null)
                return [];
            Dictionary<uint, IMessage> missions = new();
            foreach (IMessage row in tableService.GetTable<MissionData>())
                missions.TryAdd(TableFields.U(row, 1), row);
            return TableFields.List(bySeason, 2).Where(missions.ContainsKey).Select(id => missions[id]).ToList();
        }

        /// <summary>Unix start of the PlanData window of the module in this theme, or 0.</summary>
        private long PlanStart(uint theme)
        {
            var entrance = tableService.GetTable<ActivityEntranceData>().FirstOrDefault(r => TableFields.U(r, 1) == theme);
            if (entrance is null)
                return 0;
            uint moduleRow = TableFields.U(entrance, 2);
            var module = tableService.GetTable<ActivityModuleData>().FirstOrDefault(r => TableFields.U(r, 1) == moduleRow);
            if (module is null)
                return 0;
            uint plan = TableFields.Map(module, 10).Where(p => p.Key == ThemeModules.RichMan).Select(p => p.Value).FirstOrDefault();
            var planRow = tableService.GetTable<PlanData>().FirstOrDefault(r => TableFields.U(r, 1) == plan);
            return planRow is null ? 0 : (long)TableFields.U64(planRow, 4);
        }

        /// <summary>Board grids of a map (MissionMapData rows listed by MissionMapByMapIdData with column 4 = 1), keyed by point id.</summary>
        private Dictionary<uint, Grid> Board(uint map)
        {
            lock (MapCache)
            {
                if (MapCache.TryGetValue(map, out var cached))
                    return cached;
            }

            Dictionary<uint, Grid> grids = [];
            var byMap = tableService.GetTable<MissionMapByMapIdData>().FirstOrDefault(r => TableFields.U(r, 1) == map);
            if (byMap is not null)
            {
                HashSet<uint> ids = [.. TableFields.List(byMap, 2)];
                foreach (IMessage row in tableService.GetTable<MissionMapData>())
                {
                    if (!ids.Contains(TableFields.U(row, 1)) || TableFields.U(row, 4) != 1)
                        continue;
                    Grid grid = new()
                    {
                        Id = TableFields.U(row, 3),
                        Type = TableFields.U(row, 6),
                        Score = (int)TableFields.U(row, 7),
                        Spawn = TableFields.U(row, 14),
                        Slot = TableFields.U(row, 15),
                    };
                    // "next:flag,next:flag" as parsed by the client grid (flag 1 = a forward link)
                    foreach (string link in TableFields.S(row, 5).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = link.Split(':');
                        if (parts.Length == 2 && uint.TryParse(parts[0], out uint next) && parts[1].Trim() == "1")
                            grid.Next.Add(next);
                    }
                    grids.TryAdd(grid.Id, grid);
                }
            }

            lock (MapCache)
                MapCache[map] = grids;
            return grids;
        }

        private SC_RichManData Data(uint uid, uint theme)
        {
            Dictionary<uint, Record> records = Load(uid, theme);
            Game? game = LoadGame(uid, theme);
            IGCLEIEPMLE data = new();
            // every stage of the season is sent; the client locks them itself (previous stage passed and opening time)
            foreach (IMessage row in Stages(theme))
            {
                uint stage = TableFields.U(row, 1);
                records.TryGetValue(stage, out Record? record);
                data.DIPHLGDEIED[stage] = new GEFEEBGPPCM
                {
                    StageId = stage,
                    MaxScore = record?.BestScore ?? 0,
                    KNFOFFBOCML = game is not null && game.Stage == stage,
                    PGANMDGDEAP = record?.Passed ?? 0,
                };
            }

            return new SC_RichManData { ThemeId = theme, Data = data };
        }

        public override void HandleRichManData(CS_RichManData request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(Data(connection.Account.Uid, request.ThemeId));
        }

        // ----- room -----

        private static CEJJKHONOHB PlayerProto(Player p)
        {
            CEJJKHONOHB proto = new()
            {
                FCNOAJNPIJE = p.Pos,
                Score = p.Score,
                IPALKNFEKIG = p.Out,
                AGOIFELFOFA = p.Id,
            };
            // the client reads exactly three counts: normal, control and double dice
            proto.GODEMAKPOBA.AddRange(p.Dice);
            proto.LHLLCBJLMOE.AddRange(p.Cards);
            proto.GKBJCKJHDBF.AddRange(p.Cards2);
            return proto;
        }

        private static OAPDIIHNBNE BuildingProto(uint grid, Building b) => new() { EEMOOKDBFHN = grid, Id = b.Id, Owner = b.Owner };

        private MKDBDBMAKDH Room(Game game)
        {
            MKDBDBMAKDH room = new()
            {
                StageId = game.Stage,
                AOMBCDDDFFG = game.Round,
                MapId = game.Map,
                RoomId = game.RoomId,
                BDAGAHOIHHO = OGOBAEEANPC.DiceTypeNormal,
                // the client dereferences some of these sub-states without a null check
                GMODOPLJECF = new MPIKOHNDHMM(),
                LJHMFNAOCNF = new IIFMBGHKKMM(),
                GNLLICJHFAP = new NIIOMNLNJAE { MCEGEGONPFH = new ACDNPMMOFDG() },
                OMCMLHDJNBG = new HPFFMLPPBGA(),
                ICFGAFKBOIG = new ANNOIBGDHCM(),
                KCMMJMHBCJK = new EIEALJJDFPG(),
                LLHDDKCMEEM = PendingEvent(game),
            };
            room.LEMHFEGFMEB.AddRange(game.Players.Select(PlayerProto));
            foreach (var (grid, b) in game.Buildings)
                room.CLAMPHEINOA[grid] = BuildingProto(grid, b);
            room.LCMPEACGDNF.AddRange(game.Players.Where(p => p.Out).Select(p => p.Id));
            return room;
        }

        /// <summary>The event the client must run when it (re)enters the room: whatever the account is being asked.</summary>
        private static ABOCDDAJBHB PendingEvent(Game game) => game.Wait switch
        {
            Wait.Path => new ABOCDDAJBHB { Path = new JJKDLBBCELF { MapId = game.WaitGrid, LNDJKGEJIIF = (uint)game.StepsLeft } },
            Wait.Shop => ShopEvent(game),
            Wait.Build => new ABOCDDAJBHB { Build = new IDAONNGGPCD { EEMOOKDBFHN = game.WaitGrid } },
            _ => new ABOCDDAJBHB { PawnAction = new BMNOCIOIEDG { AGOIFELFOFA = 0 } },
        };

        private static ABOCDDAJBHB ShopEvent(Game game)
        {
            EKBNCEHJJCJ shop = new() { RefreshNum = game.ShopRefresh };
            shop.Goods.AddRange(game.ShopGoods);
            // ASSUMPTION: field 5 lists the goods already bought in this visit
            shop.FPKONMLJOGK.AddRange(game.ShopBought);
            return new ABOCDDAJBHB { Shop = shop };
        }

        public override void HandleRichManStartGame(CS_RichManStartGame request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? stage = Stages(request.ThemeId).FirstOrDefault(r => TableFields.U(r, 1) == request.StageId);
            if (stage is null)
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            uint previous = TableFields.U(stage, 6);
            if (previous != 0 && !(Load(uid, request.ThemeId).TryGetValue(previous, out Record? before) && before.Passed > 0))
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManStagePreStageNotPass);
                return;
            }

            // ASSUMPTION: the opening time counts from the PlanData start in the table, as the client computes it (start + minutes * 60)
            long opens = PlanStart(request.ThemeId) + TableFields.U(stage, 5) * 60L;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < opens)
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManStageTimeLock);
                return;
            }

            // a game being played must be settled first (the client settles it before starting again)
            if (LoadGame(uid, request.ThemeId) is not null)
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManGameIsPlaying);
                return;
            }

            // column 8 lists the maps of the stage (the client reads it with the same accessor as MissionMapData's map id)
            // ASSUMPTION: one of them is drawn at random. Stages without a map that has board grids cannot be played (the client would
            // load an empty board): the season 1001 stages of theme 901 have no MissionMapData in the 4.0.5142 tables.
            List<uint> maps = TableFields.List(stage, 8).Where(m => Board(m).Count > 0).ToList();
            if (maps.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManGameNotExist);
                return;
            }

            uint map = maps[Random.Shared.Next(maps.Count)];
            Dictionary<uint, Grid> board = Board(map);
            // opponents: column 11 lists their dolls; an endless stage (type 2) has column 7 of them
            int opponents = TableFields.U(stage, 4) == 2 ? (int)TableFields.U(stage, 7) : TableFields.List(stage, 11).Count;
            // ASSUMPTION: column 12 (not read by the client) is the number of normal dice of the account
            int dice = (int)TableFields.U(stage, 12);

            Game game = new()
            {
                Stage = request.StageId,
                Map = map,
                RoomId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Wait = Wait.Roll,
            };
            uint firstGrid = board.Keys.Min();
            for (uint i = 0; i <= opponents; i++)
            {
                // column 14 marks the start grid of the (i + 1)-th player
                uint start = board.Values.Where(g => g.Spawn == i + 1).Select(g => g.Id).FirstOrDefault();
                game.Players.Add(new Player
                {
                    Id = i,
                    Pos = start != 0 ? start : firstGrid,
                    Dice = i == 0 ? [dice, 0, 0] : [0, 0, 0],
                });
            }

            foreach (Grid g in board.Values.Where(g => g.Type == GridBuild))
                game.Buildings[g.Id] = new Building();

            accountState.Set(uid, GameKey(request.ThemeId), game);
            connection.Send(new SC_RichManStartGame { ThemeId = request.ThemeId, Room = Room(game) });
        }

        public override void HandleRichManResume(CS_RichManResume request, Connection connection)
        {
            if (connection.Account is null)
                return;
            Game? game = LoadGame(connection.Account.Uid, request.ThemeId);
            if (game is null)
            {
                connection.Refuse(MsgId.MsgCsRichManResume, HNINDAOJCKP.RichManGameNotExist);
                return;
            }

            connection.Send(new SC_RichManResume { ThemeId = request.ThemeId, Room = Room(game) });
        }

        // ----- commands -----

        private sealed class Turn(Game game, Dictionary<uint, Grid> board)
        {
            public readonly Game Game = game;
            public readonly Dictionary<uint, Grid> Board = board;
            public readonly List<IMessage> Out = [];

            public void Event(ABOCDDAJBHB e) => Out.Add(new SC_RichManEventNotify { GKIEAGNHFOF = e });

            public void Update(LBNADOHKJIN u) => Out.Add(new SC_RichManUpdate { HJKFGJMDMHH = u });

            public Player Self => Game.Players[0];
        }

        public override void HandleRichManCmd(CS_RichManCmd request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Game? game = LoadGame(uid, request.ThemeId);
            if (game is null)
            {
                connection.Refuse(MsgId.MsgCsRichManCmd, HNINDAOJCKP.RichManGameNotExist);
                return;
            }

            Turn turn = new(game, Board(game.Map));
            LIBHFHIMBHF code = request.Op is null ? LIBHFHIMBHF.ErrCmdNil : Execute(turn, request.Op);
            if (code == LIBHFHIMBHF.ErrNone)
                accountState.Set(uid, GameKey(request.ThemeId), game);

            connection.Send(new SC_RichManCmd { Code = code });
            foreach (IMessage message in turn.Out)
                connection.Send(message);
        }

        private LIBHFHIMBHF Execute(Turn t, IMEGLPCEECE op)
        {
            Game game = t.Game;
            if (game.Finished)
                return LIBHFHIMBHF.ErrGameFinish;

            switch (op.OpCase)
            {
                case IMEGLPCEECE.OpOneofCase.Roll:
                    return Roll(t, op.Roll);
                case IMEGLPCEECE.OpOneofCase.Direction:
                    if (game.Wait != Wait.Path)
                        return LIBHFHIMBHF.ErrOpEventNotMatch;
                    if (!t.Board.TryGetValue(game.WaitGrid, out Grid? fork) || !fork.Next.Contains(op.Direction.ILCAHDPFAHF))
                        return LIBHFHIMBHF.ErrTableInvalid;
                    game.Wait = Wait.Roll;
                    Walk(t, t.Self, game.StepsLeft, op.Direction.ILCAHDPFAHF);
                    return LIBHFHIMBHF.ErrNone;
                case IMEGLPCEECE.OpOneofCase.Purchase:
                    return Purchase(t, op.Purchase);
                case IMEGLPCEECE.OpOneofCase.RefreshShop:
                    return RefreshShop(t);
                case IMEGLPCEECE.OpOneofCase.Building:
                    return Build(t, op.Building);
                case IMEGLPCEECE.OpOneofCase.Confirm:
                    // the confirmations of the board grids handled here (shop, building) close the pending event
                    if (op.Confirm.FCBIPDJHEGA == HEFNIPKONED.Shop && game.Wait == Wait.Shop
                        || op.Confirm.FCBIPDJHEGA == HEFNIPKONED.Building && game.Wait == Wait.Build)
                    {
                        game.Wait = Wait.Roll;
                        EndTurn(t);
                        return LIBHFHIMBHF.ErrNone;
                    }
                    return LIBHFHIMBHF.ErrOpEventNotMatch;
                case IMEGLPCEECE.OpOneofCase.UseCard:
                    // ASSUMPTION: card effects (RichmanCardData buff lists -> RichmanBuffData) are not simulated; a card that is not in hand
                    // is refused as the official server does, an owned card is refused as not usable
                    return t.Self.Cards.Contains(op.UseCard.PBJBINPAONN) || t.Self.Cards2.Contains(op.UseCard.PBJBINPAONN)
                        ? LIBHFHIMBHF.ErrServerInternalFailed : LIBHFHIMBHF.ErrCardIdInvalid;
                case IMEGLPCEECE.OpOneofCase.Loot:
                    // loot grids are not simulated (see Land): there is never a loot to answer
                    return op.Loot.BIDPJDHBFGB == 0 ? LIBHFHIMBHF.ErrOpLootSelf : LIBHFHIMBHF.ErrOpEventNotMatch;
                default:
                    return LIBHFHIMBHF.ErrCmdNil;
            }
        }

        private LIBHFHIMBHF Roll(Turn t, INPBKMAMEOM roll)
        {
            Game game = t.Game;
            if (game.Wait != Wait.Roll)
                return LIBHFHIMBHF.ErrMoveAlreadyStart;

            Player self = t.Self;
            int index = roll.PHAGJFFEGBJ switch
            {
                OGOBAEEANPC.DiceTypeNormal => 0,
                OGOBAEEANPC.DiceTypeControl => 1,
                OGOBAEEANPC.DiceTypeDouble => 2,
                _ => -1,
            };
            if (index < 0)
                return LIBHFHIMBHF.ErrDiceTypeInvalid;
            if (self.Dice[index] <= 0)
                return LIBHFHIMBHF.ErrDiceNumNotEnough;

            int value;
            if (index == 1)
            {
                // the control dice lets the player pick the number (1..6 on the client dice panel)
                if (roll.NHIAEMGPHFO < 1 || roll.NHIAEMGPHFO > 6)
                    return LIBHFHIMBHF.ErrDiceTypeInvalid;
                value = roll.NHIAEMGPHFO;
            }
            else
            {
                // ASSUMPTION: the double dice rolls two dice and moves by their sum
                value = Random.Shared.Next(1, 7) + (index == 2 ? Random.Shared.Next(1, 7) : 0);
            }

            self.Dice[index]--;
            t.Event(new ABOCDDAJBHB
            {
                Roll = new PLDJBIIPCAP { NHIAEMGPHFO = value, PHAGJFFEGBJ = roll.PHAGJFFEGBJ, KKLGOHIEMED = self.Dice[index], MACPCEIELDH = 0 },
            });
            Walk(t, self, value, 0);
            return LIBHFHIMBHF.ErrNone;
        }

        /// <summary>Moves a player step by step (one Move event per grid). At a fork the account is asked the way (Path event).</summary>
        private void Walk(Turn t, Player player, int steps, uint chosen)
        {
            Game game = t.Game;
            while (steps > 0)
            {
                if (!t.Board.TryGetValue(player.Pos, out Grid? here) || here.Next.Count == 0)
                    break;
                uint next;
                if (chosen != 0)
                {
                    next = chosen;
                    chosen = 0;
                }
                else if (here.Next.Count > 1 && player.Id == 0)
                {
                    game.Wait = Wait.Path;
                    game.StepsLeft = steps;
                    game.WaitGrid = here.Id;
                    t.Event(PendingEvent(game));
                    return;
                }
                else
                {
                    // ASSUMPTION: an opponent takes a random way at a fork
                    next = here.Next[player.Id == 0 ? 0 : Random.Shared.Next(here.Next.Count)];
                }

                steps--;
                player.Pos = next;
                t.Event(new ABOCDDAJBHB { Move = new KBCHPPANOJB { EEMOOKDBFHN = next, JEAOIKCLGMN = (uint)steps, BIDPJDHBFGB = player.Id } });
            }

            Land(t, player);
        }

        /// <summary>The grid a player stops on.</summary>
        private void Land(Turn t, Player player)
        {
            Game game = t.Game;
            t.Board.TryGetValue(player.Pos, out Grid? grid);
            if (grid?.Type == GridNormal && grid.Score != 0)
            {
                // the client adds the value to the score of the player of the turn (set by PawnAction)
                player.Score += grid.Score;
                t.Event(new ABOCDDAJBHB { GridScore = new POEIBHLNGBD { PPDJJDCGJFE = grid.Score } });
            }

            if (player.Id == 0 && grid is not null)
            {
                if (grid.Type == GridShop && OpenShop(t))
                    return;
                if (grid.Type == GridBuild && game.Buildings.TryGetValue(grid.Id, out Building? b) && b.Owner < 0)
                {
                    game.Wait = Wait.Build;
                    game.WaitGrid = grid.Id;
                    t.Event(PendingEvent(game));
                    return;
                }
            }

            // ASSUMPTION: mini game, loot, random and transmit grids (RichmanEvent* tables) are not simulated: nothing happens there
            if (player.Id == 0)
                EndTurn(t);
        }

        private IMessage? StageRow(uint stage) => tableService.GetTable<MissionData>().FirstOrDefault(r => TableFields.U(r, 1) == stage);

        private bool OpenShop(Turn t)
        {
            Game game = t.Game;
            IMessage? stage = StageRow(game.Stage);
            uint shopId = stage is null ? 0 : TableFields.U(stage, 13);
            IMessage? shop = tableService.GetTable<RichmanEventShopData>().FirstOrDefault(r => TableFields.U(r, 1) == shopId);
            if (shop is null)
                return false;
            game.ShopRefresh = TableFields.U(shop, 3);
            game.ShopGoods = DrawGoods(TableFields.U(shop, 2));
            game.ShopBought = [];
            game.Wait = Wait.Shop;
            t.Event(PendingEvent(game));
            return true;
        }

        /// <summary>Shop goods: column 2 of RichmanEventShopData cards drawn by their weight (RichmanCardData column 3).</summary>
        private List<uint> DrawGoods(uint count)
        {
            List<(uint Id, int Weight)> pool = tableService.GetTable<RichmanCardData>()
                .Select(r => (TableFields.U(r, 1), TableFields.I(r, 3))).Where(c => c.Item1 != 0 && c.Item2 > 0).ToList();
            List<uint> goods = [];
            while (goods.Count < count && pool.Count > 0)
            {
                int roll = Random.Shared.Next(pool.Sum(c => c.Weight));
                int pick = 0;
                while (roll >= pool[pick].Weight)
                    roll -= pool[pick++].Weight;
                goods.Add(pool[pick].Id);
                pool.RemoveAt(pick);
            }
            return goods;
        }

        private LIBHFHIMBHF Purchase(Turn t, FOGNJDIJJIP purchase)
        {
            Game game = t.Game;
            if (game.Wait != Wait.Shop)
                return LIBHFHIMBHF.ErrOpEventNotMatch;
            // the shop panel sends (0, 0, true) when the player leaves
            if (purchase.NIJKIDFFNFP)
            {
                game.Wait = Wait.Roll;
                EndTurn(t);
                return LIBHFHIMBHF.ErrNone;
            }

            if (!game.ShopGoods.Contains(purchase.Good))
                return LIBHFHIMBHF.ErrGoodIndexInvalid;
            if (game.ShopBought.Contains(purchase.Good))
                return LIBHFHIMBHF.ErrGoodBought;
            IMessage? card = tableService.GetTable<RichmanCardData>().FirstOrDefault(r => TableFields.U(r, 1) == purchase.Good);
            if (card is null)
                return LIBHFHIMBHF.ErrTableInvalid;
            int price = (int)TableFields.U(card, 4);
            Player self = t.Self;
            if (self.Score < price)
                return LIBHFHIMBHF.ErrScoreNotEnough;

            game.ShopBought.Add(purchase.Good);
            ChangeScore(t, self, -price);
            // column 5 = 2: the card goes to the second hand (the client sorts them the same way)
            List<uint> hand = TableFields.U(card, 5) == 2 ? self.Cards2 : self.Cards;
            hand.Add(purchase.Good);
            t.Update(new LBNADOHKJIN
            {
                Card = new EHPFLEFPIHK { FNPGILFOBJN = IEOEGMANJJI.KmomjfofeddIeoegmanjjiAdd, PNJHJLOIMDJ = purchase.Good, Index = (uint)(hand.Count - 1) },
            });
            return LIBHFHIMBHF.ErrNone;
        }

        private LIBHFHIMBHF RefreshShop(Turn t)
        {
            Game game = t.Game;
            if (game.Wait != Wait.Shop)
                return LIBHFHIMBHF.ErrOpEventNotMatch;
            if (game.ShopRefresh == 0)
                return LIBHFHIMBHF.ErrRefreshNumNotEnough;
            game.ShopRefresh--;
            IMessage? stage = StageRow(game.Stage);
            IMessage? shop = tableService.GetTable<RichmanEventShopData>().FirstOrDefault(r => TableFields.U(r, 1) == (stage is null ? 0 : TableFields.U(stage, 13)));
            game.ShopGoods = DrawGoods(shop is null ? (uint)game.ShopGoods.Count : TableFields.U(shop, 2));
            game.ShopBought = [];
            t.Event(PendingEvent(game));
            return LIBHFHIMBHF.ErrNone;
        }

        private LIBHFHIMBHF Build(Turn t, BENKENMOFOF build)
        {
            Game game = t.Game;
            if (game.Wait != Wait.Build)
                return LIBHFHIMBHF.ErrOpEventNotMatch;
            // the building panel sends (0, true) when the player builds nothing
            if (build.NIJKIDFFNFP)
            {
                game.Wait = Wait.Roll;
                EndTurn(t);
                return LIBHFHIMBHF.ErrNone;
            }

            IMessage? row = tableService.GetTable<RichmanBuildingData>().FirstOrDefault(r => TableFields.U(r, 1) == build.PMBOHEIDKAC);
            if (row is null || build.PMBOHEIDKAC == 0)
                return LIBHFHIMBHF.ErrBuildingIdInvalid;
            Player self = t.Self;
            if (game.Buildings.Values.Any(b => b.Owner == 0 && b.Id == build.PMBOHEIDKAC))
                return LIBHFHIMBHF.ErrBuildingIdDuplicate;
            int price = (int)TableFields.U(row, 8);
            if (self.Score < price)
                return LIBHFHIMBHF.ErrScoreNotEnough;

            ChangeScore(t, self, -price);
            Building building = game.Buildings[game.WaitGrid];
            building.Id = build.PMBOHEIDKAC;
            building.Owner = 0;
            t.Event(new ABOCDDAJBHB
            {
                BuildingBuild = new DCAKOCEBPPL { JKFNEDPMPGL = BuildingProto(game.WaitGrid, building), CIHEKKMPIKI = HKACEMFDGHM.BuildTypeBuild },
            });
            game.Wait = Wait.Roll;
            EndTurn(t);
            return LIBHFHIMBHF.ErrNone;
        }

        private static void ChangeScore(Turn t, Player player, int delta)
        {
            player.Score += delta;
            t.Update(new LBNADOHKJIN { Score = new KOIMJIGHONL { EDECKGACHKF = delta, CNHHLFKHKHC = player.Score, AGOIFELFOFA = player.Id } });
        }

        /// <summary>End of the account's action: the opponents play, then the next round gives the hand back to the account.</summary>
        private void EndTurn(Turn t)
        {
            Game game = t.Game;
            t.Event(new ABOCDDAJBHB { PlayerActionEnd = new KGENAGMCJDN() });
            foreach (Player ai in game.Players.Where(p => p.Id != 0 && !p.Out))
            {
                t.Event(new ABOCDDAJBHB { PawnAction = new BMNOCIOIEDG { AGOIFELFOFA = ai.Id } });
                int value = Random.Shared.Next(1, 7);
                t.Event(new ABOCDDAJBHB
                {
                    Roll = new PLDJBIIPCAP { NHIAEMGPHFO = value, PHAGJFFEGBJ = OGOBAEEANPC.DiceTypeNormal, MACPCEIELDH = ai.Id },
                });
                Walk(t, ai, value, 0);
                t.Event(new ABOCDDAJBHB { PlayerActionEnd = new KGENAGMCJDN() });
            }

            Player self = t.Self;
            if (self.Dice.Sum() <= 0)
            {
                // no dice left: the client sees the end itself when the hand comes back and asks SettleGame
                game.Finished = true;
            }
            else
            {
                game.Round++;
                t.Event(new ABOCDDAJBHB { Round = new IBJIIHJCCFH { AOMBCDDDFFG = game.Round } });
            }

            game.Wait = Wait.Roll;
            t.Event(new ABOCDDAJBHB { PawnAction = new BMNOCIOIEDG { AGOIFELFOFA = 0 } });
        }

        // ----- settle -----

        public override void HandleRichManSettleGame(CS_RichManSettleGame request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Game? game = LoadGame(uid, request.ThemeId);
            if (game is null)
            {
                connection.Refuse(MsgId.MsgCsRichManSettleGame, HNINDAOJCKP.RichManGameNotExist);
                return;
            }

            Player self = game.Players[0];
            // the final score the client shows: own score plus the value of the own buildings (RichmanBuildingData column 10)
            Dictionary<uint, IMessage> buildingRows = tableService.GetTable<RichmanBuildingData>()
                .GroupBy(r => TableFields.U(r, 1)).ToDictionary(g => g.Key, g => (IMessage)g.First());
            int buildings = game.Buildings.Values.Where(b => b.Owner == 0 && buildingRows.ContainsKey(b.Id))
                .Sum(b => (int)TableFields.U(buildingRows[b.Id], 10));
            int score = self.Score + buildings;
            // ASSUMPTION: the rank is the place of the account by score among the players (1 = first)
            int rank = 1 + game.Players.Count(p => p.Id != 0 && p.Score > score);

            Dictionary<uint, Record> records = Load(uid, request.ThemeId);
            if (!records.TryGetValue(game.Stage, out Record? record))
                records[game.Stage] = record = new Record();
            record.BestScore = Math.Max(record.BestScore, score);
            // ASSUMPTION: a stage counts as passed when its game was played to the end (no dice left) without the account being out
            if (game.Finished && !self.Out)
                record.Passed++;
            accountState.Set(uid, Key(request.ThemeId), records);
            accountState.Delete(uid, GameKey(request.ThemeId));

            SC_RichManSettleGame settle = new()
            {
                ThemeId = request.ThemeId,
                StageId = game.Stage,
                LPGIKNAIEMB = score,
                HGNPJOLACNN = rank,
            };
            settle.PBLGMIPDEBP[(int)JIHGBCKKLMD.RichManSettleValueKeyRound] = (int)game.Round;
            connection.Send(settle);
            connection.Send(Data(uid, request.ThemeId));
        }

        public override void HandleRichManRank(CS_RichManRank request, Connection connection)
        {
            // a local server has no other players to rank against; the client adds the own endless score to the list itself
            if (connection.Account is not null)
                connection.Send(new SC_RichManRank { ThemeId = request.ThemeId });
        }
    }
}
