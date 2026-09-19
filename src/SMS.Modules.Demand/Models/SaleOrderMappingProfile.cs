using AutoMapper;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Models;

// A29-P3-05. Read-only direction only (entity -> model) — there is no write path yet for this
// task's scope, so a model -> entity map would have nothing to call it and nothing to verify it
// against, unlike BusinessPartnerMappingProfile's two-way map in Suppliers (P1-03), which backs a
// real Create/Update flow already built in that same task.
internal sealed class SaleOrderMappingProfile : Profile
{
    public SaleOrderMappingProfile()
    {
        CreateMap<SaleOrder, SaleOrderModel>()
            .ForMember(d => d.Uuid, opt => opt.MapFrom(s => s.UUID));

        CreateMap<SaleOrderLine, SaleOrderLineModel>()
            .ForMember(d => d.Uuid, opt => opt.MapFrom(s => s.UUID));
    }
}
