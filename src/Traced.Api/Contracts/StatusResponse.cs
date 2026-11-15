namespace Traced.Api.Contracts;

/// <summary>Spec <c>StatusResponse</c>: returned by endpoints with no resource to return.</summary>
public sealed record StatusResponse(string Status)
{
    public static readonly StatusResponse Ok = new("ok");
}
