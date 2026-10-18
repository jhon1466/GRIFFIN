using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class SupportHandler(IAccountStateService accountState) : SupportHandlerBase
    {
        public override void HandleSupportRoomUpgrade(CS_SupportRoomUpgrade request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportRoomUpgrade, HNINDAOJCKP.SupportRoomLevelNotFound);

        public override void HandleSupportClaimSlotDefaultTeam(CS_SupportClaimSlotDefaultTeam request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportClaimSlotDefaultTeam, HNINDAOJCKP.SupportTeamSlotLocked);

        public override void HandleSupportAmmunitionCompose(CS_SupportAmmunitionCompose request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsSupportAmmunitionCompose, HNINDAOJCKP.SupportNotFound);

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
                            HLCFEAGODDH = 1,
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
                        }

                        roomInfo.ABOENIKPJLH.IFMGFOIGHNM = SupportChips.BattleFormation(accountState, uid);   // field 9 = the formation used in battle (field 2 is the room level)
                    }

                    connection.Send(1, roomInfo);
        }

        public override void HandleSupportTeams(CS_SupportTeams request, Connection connection)
        {
                    connection.Send(new SC_SupportTeams
                    {
                        NJMEBOGONKG = { },
                    });
        }
    }
}
