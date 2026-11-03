using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The character shown on the main lobby screen (the "adjutant"). The client asks for it every time it returns to the lobby, so the
    /// choice (<c>CS_AdjutantSet</c>: character and costume; <c>CS_BackAdjutantSet</c>: the four side slots) is read back from the
    /// preferences stored by <see cref="PreferenceResponder"/>; before this the answer was a constant and every change was undone on return.
    /// </summary>
    public sealed class AdjutantHandler(IAccountStateService accountState) : AdjutantHandlerBase
    {
        private const int SlotCount = 4;

        public override void HandleAdjutantInfo(CS_AdjutantInfo request, Connection connection)
        {
            var adjutant = new Adjutant
            {
                Current = 0,
                Costume = 0,
                Assistants = { 10202, 0, 0, 0 },
                Background = new AdjutantBackground
                {
                    Inside = 1001,
                    Outside = 2001
                }
            };

            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                if (PreferenceResponder.Recall<CS_AdjutantSet>(accountState, uid, "AdjutantSet") is { } set && set.AdjutantId != 0)
                {
                    adjutant.Current = set.AdjutantId;
                    adjutant.Costume = set.CostumeId;
                }

                if (PreferenceResponder.Recall<CS_BackAdjutantSet>(accountState, uid, "BackAdjutantSet") is { } back)
                {
                    SetUniqueAssistants(adjutant, back.Adjutants);
                }
            }

            // The game can leave a newly acquired/equipped adjutant in a side slot. Sending
            // the same gun as Current and Assistant makes the lobby instantiate two models
            // at the same anchor. Keep the primary choice and compact only distinct side slots.
            SetUniqueAssistants(adjutant, adjutant.Assistants, adjutant.Assistants.Count);

            connection.Send(new SC_AdjutantInfo { Adjutant = adjutant });
        }

        private static void SetUniqueAssistants(Adjutant adjutant, IEnumerable<uint> slots, int count = SlotCount)
        {
            uint[] requestedSlots = slots.Take(count).ToArray();
            var seen = new HashSet<uint>();
            if (adjutant.Current != 0)
                seen.Add(adjutant.Current);

            adjutant.Assistants.Clear();
            foreach (uint slot in requestedSlots)
            {
                if (slot != 0 && !seen.Add(slot))
                    continue;
                adjutant.Assistants.Add(slot);
                if (adjutant.Assistants.Count == count)
                    break;
            }

            while (adjutant.Assistants.Count < count)
                adjutant.Assistants.Add(0);
        }
    }
}
