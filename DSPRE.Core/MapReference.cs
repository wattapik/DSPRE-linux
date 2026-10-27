namespace DSPRE.Core;

public sealed record MapReference(ushort MapId, string Name)
{
    public override string ToString() => $"{MapId:D4} - {Name}";
}
