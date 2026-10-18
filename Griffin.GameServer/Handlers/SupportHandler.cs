using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class SupportHandler(IAccountStateService accountState, Griffin.Common.Table.ITableService tableService, IItemUseService itemUse, IRewardService rewardService) : SupportHandlerBase
    {
        /// <summary>One level of the room ("阵列规模"): costs the item of <c>SupportLevelData</c> and answers the new level (the only field the client reads, A-94).</summary>
        public override void HandleSupportRoomUpgrade(CS_SupportRoomUpgrade request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint level = SupportTeams.RoomLevel(accountState, uid);
            // the request carries the level the player chose in the dialog (several levels at once); an old client value of 0 means one level
            uint target = request.JLHFICPMAEI > level ? request.JLHFICPMAEI : level + 1;
            if (SupportTeams.UpgradeCost(tableService, level, target) is not { } cost)
            {
                connection.Refuse(MsgId.MsgCsSupportRoomUpgrade, HNINDAOJCKP.SupportRoomLevelNotFound);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [];
            if (!itemUse.Spend(uid, new Dictionary<uint, int> { [cost.Item] = cost.Amount }, messages))
            {
                connection.Refuse(MsgId.MsgCsSupportRoomUpgrade, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            level = target;
            accountState.Set<uint?>(uid, SupportTeams.LevelKey, level);
            messages.Add(new SC_SupportRoomUpgrade { JLHFICPMAEI = level });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleSupportClaimSlotDefaultTeam(CS_SupportClaimSlotDefaultTeam request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportClaimSlotDefaultTeam, HNINDAOJCKP.SupportTeamSlotLocked);

        /// <summary>
        /// "补给制造": makes <c>num</c> units of an ammunition (<c>SupportAmmunitionData</c>) from its recipe: column 8 = {ammunition of the tier below, amount} and column 9 = {material item, amount},
        /// both per unit; the ammunition is an ordinary item (A-96). The ammunition of the first tier (no recipe) is free and cannot be made.
        /// </summary>
        public override void HandleSupportAmmunitionCompose(CS_SupportAmmunitionCompose request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Griffin.Common.Table.SupportAmmunitionData? row = tableService.GetTable<Griffin.Common.Table.SupportAmmunitionData>()
                .FirstOrDefault(r => Griffin.GameServer.Services.TableFields.U(r, 1) == request.Id);
            IReadOnlyList<(uint Key, uint Value)> material = row is null ? [] : Griffin.GameServer.Services.TableFields.Map(row, 9);
            if (row is null || request.BAGFCBNOMCO == 0 || material.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsSupportAmmunitionCompose, HNINDAOJCKP.SupportNotFound);
                return;
            }

            Dictionary<uint, int> cost = [];
            foreach ((uint item, uint count) in Griffin.GameServer.Services.TableFields.Map(row, 8).Concat(material))
                cost[item] = cost.GetValueOrDefault(item) + (int)(count * request.BAGFCBNOMCO);

            List<Google.Protobuf.IMessage> messages = [];
            if (!itemUse.Spend(uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsSupportAmmunitionCompose, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            rewardService.Grant(uid, request.Id, (int)request.BAGFCBNOMCO, messages);
            messages.Add(new SC_SupportAmmunitionCompose { Id = request.Id, Num = request.BAGFCBNOMCO });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleSupportRoomInfo(CS_SupportRoomInfo request, Connection connection)
        {
                    SC_SupportRoomInfo roomInfo = new SC_SupportRoomInfo
                    {
                        ABOENIKPJLH = new MIFHFBCEDCD
                        {
                            KMPIHKFBAEP =
                    {
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [3] = 20,
                                [4] = 20,
                                [5] = 20,
                                [6] = 20,
                                [1] = 20,
                                [2] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [6] = 20,
                                [1] = 20,
                                [2] = 20,
                                [3] = 20,
                                [4] = 20,
                                [5] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [1] = 20,
                                [2] = 20,
                                [3] = 20,
                                [4] = 20,
                                [5] = 20,
                                [6] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [2] = 20,
                                [3] = 20,
                                [4] = 20,
                                [5] = 20,
                                [6] = 20,
                                [1] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [4] = 20,
                                [5] = 20,
                                [6] = 20,
                                [1] = 20,
                                [2] = 20,
                                [3] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [4] = 20,
                                [5] = 20,
                                [6] = 20,
                                [1] = 20,
                                [2] = 20,
                                [3] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                        new OMBHFHECHOO
                        {
                            Name = "",
                            CDLNJHGCDMK =
                            {
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                                new EFLMEODNBLD
                                {
                                    COCHLPMJPOJ = 0,
                                    LOOPDHFNOIH = 0,
                                    JIHKJNEPGBB =
                                    {
                                    },
                                },
                            },
                            GDLFJIJOMAL =
                            {
                                [2] = 20,
                                [3] = 20,
                                [4] = 20,
                                [5] = 20,
                                [6] = 20,
                                [1] = 20,
                            },
                            AHICNPGNNOC = 0,
                            JELCIHPBECA =
                            {
                            },
                            ENFCKIOLJNM =
                            {
                            },
                        },
                    },
                            HLCFEAGODDH = connection.Account is null ? 1 : SupportTeams.RoomLevel(accountState, connection.Account.Uid),
                            JJGNHDJNMJM =
                    {
                    },
                            IFMGFOIGHNM = 1,
                        },
                    };

                    // the core chips of each formation (A-77) and the formation used in battle
                    if (connection.Account is not null)
                    {
                        uint uid = connection.Account.Uid;
                        for (int index = 0; index < roomInfo.ABOENIKPJLH.KMPIHKFBAEP.Count; index++)
                        {
                            roomInfo.ABOENIKPJLH.KMPIHKFBAEP[index].ENFCKIOLJNM.Clear();
                            roomInfo.ABOENIKPJLH.KMPIHKFBAEP[index].ENFCKIOLJNM.AddRange(SupportChips.FormationChips(accountState, uid, (uint)index + 1));
                            SupportTeams.Fill(accountState, uid, (uint)index + 1, roomInfo.ABOENIKPJLH.KMPIHKFBAEP[index]);
                            SupportBaseChips.Fill(accountState, uid, (uint)index + 1, roomInfo.ABOENIKPJLH.KMPIHKFBAEP[index]);
                        }

                        roomInfo.ABOENIKPJLH.IFMGFOIGHNM = SupportChips.BattleFormation(accountState, uid);   // field 9 = the formation used in battle (field 2 is the room level)
                    }

                    connection.Send(1, roomInfo);
        }

        public override void HandleSupportTeams(CS_SupportTeams request, Connection connection)
        {
                    SC_SupportTeams teams = new();
                    teams.NJMEBOGONKG.AddRange(SupportTeams.Owned(tableService));
                    connection.Send(teams);
        }
    }
}
