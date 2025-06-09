namespace Warp.Core.Data;

public interface IUser : IEntity
{
    string Email { get; set; }
    List<string> Permissions { get; }
}