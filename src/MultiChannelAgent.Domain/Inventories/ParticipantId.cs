namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Strongly typed Participant identity. Equal to the Participant's immutable Microsoft Entra object
/// ID: the single tenant's Entra directory is the sole source of Participant identity, so this never
/// varies across channels or sessions for the same person.
/// </summary>
public readonly record struct ParticipantId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
