using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class UavHandler(IUavService uav) : UavHandlerBase
    {
        public override void HandleUavRevelant(CS_UavRevelant request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_UavRevelant { UavRevelant = uav.Get(connection.Account.Uid) });
        }

        public override void HandleUavUpGrade(CS_UavUpGrade request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (uav.UpGrade(connection.Account.Uid, messages, out HNINDAOJCKP error) is not { } grade)
            {
                connection.Refuse(MsgId.MsgCsUavUpGrade, error);
                return;
            }

            messages.Add(new SC_UavUpGrade { Grade = grade });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }

    public sealed class UavArmHandler(IUavService uav) : UavArmHandlerBase
    {
        public override void HandleUnlockUavArm(CS_UnlockUavArm request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (uav.UnlockArm(connection.Account.Uid, request.ArmId, out HNINDAOJCKP error) is not { } arm)
            {
                connection.Refuse(MsgId.MsgCsUnlockUavArm, error);
                return;
            }

            connection.Send(new SC_UnlockUavArm { Arm = arm });
        }

        public override void HandleUavArmLevelUp(CS_UavArmLevelUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (uav.LevelUp(connection.Account.Uid, request.Id, messages, out HNINDAOJCKP error) is not { } arm)
            {
                connection.Refuse(MsgId.MsgCsUavArmLevelUp, error);
                return;
            }

            messages.Add(new SC_UavArmLevelUp { Arm = arm });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleUavArmInstall(CS_UavArmInstall request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (uav.Install(connection.Account.Uid, request.Position, request.ArmId, out HNINDAOJCKP error) is not { } equips)
            {
                connection.Refuse(MsgId.MsgCsUavArmInstall, error);
                return;
            }

            SC_UavArmInstall reply = new();
            reply.UavEquips.AddRange(equips);
            connection.Send(reply);
        }
    }
}
