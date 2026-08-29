using LiteOrm.Common;
using LiteOrm.Service;
using LiteOrm.WebDemo.Models;

namespace LiteOrm.WebDemo.Services;

[Service]
public interface IDemoAuthSessionService :
    IEntityServiceAsync<DemoAuthSession>,
    IEntityViewServiceAsync<DemoAuthSession>
{
}

[AutoRegister(Lifetime = Lifetime.Scoped)]
public class DemoAuthSessionService(IServiceProvider serviceProvider) : EntityService<DemoAuthSession>(serviceProvider), IDemoAuthSessionService
{
}
