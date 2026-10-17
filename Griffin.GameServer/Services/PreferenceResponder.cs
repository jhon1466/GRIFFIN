using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Protocol;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Settings, cosmetic choices, formation names and "read" marks: requests whose answer is just the value that was sent.
    /// The value is stored per account (<c>prefs</c> in <c>AccountStates</c>, last request of each kind) and echoed back with
    /// the fields the response shares with the request. Only names on <see cref="Names"/> are handled; rewards, purchases and
    /// anything that changes game progress never go through here.
    /// </summary>
    public sealed class PreferenceResponder(IAccountStateService accountState) : IFallbackResponder
    {
        private const string StateKey = "prefs";

        /// <summary>Requests (without the MsgCs prefix) that are plain preferences or marks.</summary>
        public static readonly HashSet<string> Names =
        [
            "ActivityAmoTescoUpdateHistoryText", "ActivityCheckInMessageOver", "AdjutantSet", "AfkAssistantSetInfo", "AfkAssistantSetSeq",
            "AssistantSet", "BackAdjutantSet", "GachaSelectUp", "BattleHeartBeat", "ChangeLanguage", "ChatDeleteMessage",
            "ControlRoomSetWeaponExhibition", "CustomFormationModify", "CustomFormationModifyName", "FormationAiPresetModify",
            "FriendSetMark", "GunBattleAiPresetUpdate", "GunRemouldingPluginDelLockPlan", "GunRemouldingPluginLockPlan",
            "GunRemouldingPluginSetLockPlan", "GunWeaponModRedDotClear", "HeartBeat", "LobbyApplySchemeV3",
            "LobbyBedRoomRebuildTaskRedPoint", "LobbyBirthdayCarePlayedNtf", "LobbyCafeFavourite", "LobbyCharacterFavouriteGun",
            "LobbyChoosePhoto", "LobbyFinishEvent", "LobbyDishIndexFavorite", "NpcChangeCostume", "LobbyOldSchemeTransformShareCode", "LobbySaveEvent", "LobbySaveSchemeV3", "LobbySaveShareCode",
            "LobbySchemeModifyName", "LobbySetCustomizeRoom", "LobbySetGunFavourite", "LobbySetSchemeV2",
            "LobbyShareCodeModifyName", "LockGunPublicTalentItem", "NamingCardGroup", "ReadNewDailyQuest", "SetAutoDismantleModRule", "TutorialSend", "SetBgm",
            "SetHandmadeCompart", "SetHandmadeData", "SetHandmadeWallPaper", "SetLobbyBgm", "SetLobbyMeilingConfig",
            "SetLobbyPhotoCut", "SetNameCardCollectionSlots", "SetNameCardGameplaySlots", "SetNameCardTheme",
            "SetPersonalInformationSetting", "SocialBlackRemove", "SocialBlackSet", "SupportAmmunitionAuto",
            "SupportChipDelLockPlan", "SupportChipLockPlan", "SupportChipSetLockPlan", "SupportFormationAmmunitionSequence",
            "SupportFormationModifyBaseChip", "SupportFormationModifyName", "SupportFormationSetBattleFormationId",
            "ThemeMiniGameRecordRedPoint", "TutorialOssClick", "UpdateSettingEndlessTowerShare", "UpdateSettingGacheAutoShare",
        ];

        public bool TryRespond(Connection connection, BasePacket packet)
        {
            string name = packet.MsgId.ToString();
            if (!name.StartsWith("MsgCs", StringComparison.Ordinal) || !Names.Contains(name[5..]))
                return false;

            if (!Enum.TryParse("MsgSc" + name[5..], out MsgId replyId) || BasePacket.CreateEmptyMessage(replyId) is not { } reply)
            {
                // a request with no response message (e.g. a heartbeat or a mark): store it, nothing to send back
                Remember(connection, name[5..], packet);
                return true;
            }

            Remember(connection, name[5..], packet);
            Copy(packet.Message!, reply);
            connection.SendAutoEncrypted<IMessage>(reply);
            Log.Information("Preference {Request} stored and echoed for account {Uid}.", name, connection.Account?.Uid);
            return true;
        }

        /// <summary>The last stored request of this kind (e.g. "SetBgm"), or null.</summary>
        public static T? Recall<T>(IAccountStateService state, uint accountUid, string request) where T : class, IMessage<T>, new()
        {
            Dictionary<string, string>? prefs = state.Get<Dictionary<string, string>>(accountUid, StateKey);
            if (prefs is null || !prefs.TryGetValue(request, out string? base64))
                return null;

            return new MessageParser<T>(() => new T()).ParseFrom(Convert.FromBase64String(base64));
        }

        private void Remember(Connection connection, string request, BasePacket packet)
        {
            if (connection.Account is null || packet.Message is null)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<string, string> prefs = accountState.Get<Dictionary<string, string>>(uid, StateKey) ?? [];
            prefs[request] = Convert.ToBase64String(packet.Message.ToByteArray());
            accountState.Set(uid, StateKey, prefs);
        }

        /// <summary>Copies every field of <paramref name="from"/> into the same-named, same-typed field of <paramref name="to"/>.</summary>
        internal static void Copy(IMessage from, IMessage to)
        {
            foreach (FieldDescriptor target in to.Descriptor.Fields.InFieldNumberOrder())
            {
                FieldDescriptor? source = from.Descriptor.Fields.InFieldNumberOrder().FirstOrDefault(field =>
                    field.Name == target.Name
                    && field.FieldType == target.FieldType
                    && field.IsRepeated == target.IsRepeated
                    && field.IsMap == target.IsMap);
                if (source is null)
                    continue;

                try
                {
                    object? value = source.Accessor.GetValue(from);
                    if (target.IsMap)
                    {
                        IDictionary destination = (IDictionary)target.Accessor.GetValue(to);
                        foreach (DictionaryEntry entry in (IDictionary)value)
                            destination[entry.Key] = entry.Value;
                    }
                    else if (target.IsRepeated)
                    {
                        IList destination = (IList)target.Accessor.GetValue(to);
                        foreach (object item in (IEnumerable)value)
                            destination.Add(item);
                    }
                    else if (value is not null)
                    {
                        target.Accessor.SetValue(to, value);
                    }
                }
                catch (Exception ex) when (ex is InvalidCastException or ArgumentException or InvalidOperationException)
                {
                    Log.Warning("Preference echo could not copy field {Field} of {From} into {To}: {Error}", target.Name, from.Descriptor.Name, to.Descriptor.Name, ex.Message);
                }
            }
        }
    }
}
