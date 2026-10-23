using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    // Player-card changes. Each message has its own generated handler base, hence one small class per message.
    // The change is stored (and shown after the next login) and echoed to the client. The official server also validates
    // ownership and charges for some changes (sex change costs an item); that is not in the client tables and is not done.

    public sealed class NameHandler(IProfileService profile) : NameHandlerBase
    {
        private const int MaxNameLength = 24;

        public override void HandleModName(CS_ModName request, Connection connection)
        {
            string name = (request.Name ?? string.Empty).Trim();
            if (connection.Account is null || name.Length == 0 || name.Length > MaxNameLength)
            {
                Log.Warning("ModName rejected: '{Name}' (empty or longer than {Max}).", name, MaxNameLength);
                connection.Refuse(MsgId.MsgCsModName, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            profile.Update(connection.Account.Uid, state => state.Name = name);
            connection.Send(new SC_ModName { Name = name });
        }
    }

    public sealed class PortraitHandler(IProfileService profile, ITableService tableService) : PortraitHandlerBase
    {
        public override void HandleModPortrait(CS_ModPortrait request, Connection connection)
        {
            if (connection.Account is null || !tableService.GetTable<ItemData>().Any(i => i.Id == request.Portrait))
            {
                Log.Warning("ModPortrait rejected: item {Portrait} is not in ItemData.", request.Portrait);
                connection.Refuse(MsgId.MsgCsModPortrait, HNINDAOJCKP.UserPortraitNotFound);
                return;
            }

            profile.Update(connection.Account.Uid, state => state.Portrait = request.Portrait);
            connection.Send(new SC_ModPortrait { Portrait = request.Portrait });
        }
    }

    public sealed class PortraitFrameHandler(IProfileService profile, ITableService tableService) : PortraitFrameHandlerBase
    {
        public override void HandleModPortraitFrame(CS_ModPortraitFrame request, Connection connection)
        {
            if (connection.Account is null || !tableService.GetTable<ItemData>().Any(i => i.Id == request.PortraitFrame))
            {
                Log.Warning("ModPortraitFrame rejected: item {Frame} is not in ItemData.", request.PortraitFrame);
                connection.Refuse(MsgId.MsgCsModPortraitFrame, HNINDAOJCKP.UserPortraitNotFound);
                return;
            }

            profile.Update(connection.Account.Uid, state => state.PortraitFrame = request.PortraitFrame);
            connection.Send(new SC_ModPortraitFrame { PortraitFrame = request.PortraitFrame });
        }
    }

    public sealed class PlayerMottoHandler(IProfileService profile) : PlayerMottoHandlerBase
    {
        private const int MaxMottoLength = 60;

        public override void HandleModPlayerMotto(CS_ModPlayerMotto request, Connection connection)
        {
            string motto = (request.PlayerMotto ?? string.Empty).Trim();
            if (connection.Account is null || motto.Length > MaxMottoLength)
            {
                Log.Warning("ModPlayerMotto rejected (longer than {Max}).", MaxMottoLength);
                return;
            }

            profile.Update(connection.Account.Uid, state => state.Motto = motto);
            connection.Send(new SC_ModPlayerMotto { PlayerMotto = motto });
        }
    }

    public sealed class SexHandler(IProfileService profile, IItemUseService itemUse) : SexHandlerBase
    {
        public override void HandleModSex(CS_ModSex request, Connection connection)
        {
            if (connection.Account is null)
                return;

            // the change card (item 110051) is spent when the client names it; without one in the bag the change is refused
            var messages = new List<Google.Protobuf.IMessage>();
            if (request.ItemId != 0 && !itemUse.Spend(connection.Account.Uid, new Dictionary<uint, int> { [request.ItemId] = (int)Math.Max(request.Num, 1u) }, messages))
            {
                connection.Refuse(MsgId.MsgCsModSex, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            profile.Update(connection.Account.Uid, state => state.Sex = (int)request.Sex);
            // the client applies the item delta itself: the answer goes first
            messages.Insert(0, new SC_ModSex { Sex = request.Sex });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }

    public sealed class BirthdayHandler(IProfileService profile) : BirthdayHandlerBase
    {
        public override void HandleModBirthday(CS_ModBirthday request, Connection connection)
        {
            if (connection.Account is null)
                return;

            profile.Update(connection.Account.Uid, state => state.Birthday = request.Birthday);
            connection.Send(new SC_ModBirthday { Birthday = request.Birthday });
        }
    }

    public sealed class BirthdayHideHandler(IProfileService profile) : BirthdayHideHandlerBase
    {
        public override void HandleModBirthdayHide(CS_ModBirthdayHide request, Connection connection)
        {
            if (connection.Account is null)
                return;

            profile.Update(connection.Account.Uid, state => state.BirthdayHidden = request.CJIMFIJEJKL);
            connection.Send(new SC_ModBirthdayHide { CJIMFIJEJKL = request.CJIMFIJEJKL });
        }
    }

    public sealed class MetalHandler(IProfileService profile) : MetalHandlerBase
    {
        public override void HandleModMetal(CS_ModMetal request, Connection connection)
        {
            if (connection.Account is null)
                return;

            profile.Update(connection.Account.Uid, state => state.Medal = request.Metal);
            connection.Send(new SC_ModMetal { Metal = request.Metal });
        }
    }

    public sealed class BackgroundCardHandler(IProfileService profile) : BackgroundCardHandlerBase
    {
        public override void HandleModBackgroundCard(CS_ModBackgroundCard request, Connection connection)
        {
            if (connection.Account is null)
                return;

            profile.Update(connection.Account.Uid, state => state.BackgroundCard = request.Id);
            connection.Send(new SC_ModBackgroundCard { Id = request.Id });
        }
    }
}
