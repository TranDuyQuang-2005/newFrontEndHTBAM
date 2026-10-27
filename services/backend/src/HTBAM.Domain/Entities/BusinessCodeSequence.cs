namespace HTBAM.Domain.Entities;

public sealed class BusinessCodeSequence
{
    public string Scope { get; set; } = "";
    public int LastNumber { get; set; }
}
