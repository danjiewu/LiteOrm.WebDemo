using LiteOrm.Common;
using LiteOrm.Service;
using LiteOrm.WebDemo.Models;

namespace LiteOrm.WebDemo.Services;

[Service]
public interface IDemoDepartmentService :
    IEntityServiceAsync<DemoDepartment>,
    IEntityViewServiceAsync<DemoDepartment>
{
}

[AutoRegister(Lifetime = Lifetime.Scoped)]
public class DemoDepartmentService(IServiceProvider serviceProvider) : EntityService<DemoDepartment>(serviceProvider), IDemoDepartmentService
{
}
