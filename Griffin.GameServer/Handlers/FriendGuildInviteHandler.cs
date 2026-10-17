using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;

namespace Griffin.GameServer.Handlers;

/// <summary>Guild invitations are unavailable in the single-account local server.</summary>
public sealed class FriendGuildInviteHandler : FriendGuildInviteHandlerBase
{
    public override void HandleFriendGuildInviteList(CS_FriendGuildInviteList request, Connection connection) =>
        connection.Send(new SC_FriendGuildInviteList());

    public override void HandleFriendGuildInvite(CS_FriendGuildInvite request, Connection connection) =>
        connection.Refuse(MsgId.MsgCsFriendGuildInvite, HNINDAOJCKP.GuildNotFound);

    public override void HandleFriendGuildInviteOperation(CS_FriendGuildInviteOperation request, Connection connection) =>
        connection.Refuse(MsgId.MsgCsFriendGuildInviteOperation, HNINDAOJCKP.GuildInvitationNotFound);
}
