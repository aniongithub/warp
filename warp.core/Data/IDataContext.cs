using System.Linq.Expressions;

namespace Warp.Core.Data;

public interface IDataContext
{
    IQueryable<IUser> Users { get; }
    IQueryable<IApiKey> ApiKeys { get; }
    IQueryable<IRequest> Requests { get; }
    Task SaveAsync<T>(T entity) where T : IEntity;
    Task UpsertAsync<T>(T entity, Expression<Func<T, bool>> filter) where T : IEntity;

    IUser CreateUser();
    IApiKey CreateApiKey();
    IRequest CreateRequest();
}
