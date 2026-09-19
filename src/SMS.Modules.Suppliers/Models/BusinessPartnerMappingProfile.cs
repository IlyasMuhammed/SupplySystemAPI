using AutoMapper;
using SMS.Modules.Suppliers.Domain;

namespace SMS.Modules.Suppliers.Models;

// P1-03 (Addendum 29 §1.2/§1.3). Mirrors SMS.Modules.Auth.Models.AuthMappingProfile — the one
// other place in the codebase using AutoMapper (every other module hand-writes its DTO mapping in
// the service layer; see the task notes for why this task uses AutoMapper anyway: it is exactly
// what was asked for, the infrastructure is already registered and already scans this assembly
// via SMS.API's global AddAutoMapper call, and nothing here is already covered by a different
// pattern the way credit_limit/payment_terms_days were in P1-02).
internal sealed class BusinessPartnerMappingProfile : Profile
{
    public BusinessPartnerMappingProfile()
    {
        CreateMap<BusinessPartner, BusinessPartnerModel>()
            .ForMember(d => d.Uuid,        opt => opt.MapFrom(s => s.UUID))
            .ForMember(d => d.PartnerCode, opt => opt.MapFrom(s => s.SupplierCode))
            .ForMember(d => d.CompanyName, opt => opt.MapFrom(s => s.SupplierName));

        CreateMap<BusinessPartnerModel, BusinessPartner>()
            .ForMember(d => d.Id,             opt => opt.Ignore())
            .ForMember(d => d.UUID,           opt => opt.MapFrom(s => s.Uuid))
            .ForMember(d => d.SupplierCode,   opt => opt.MapFrom(s => s.PartnerCode))
            .ForMember(d => d.SupplierName,   opt => opt.MapFrom(s => s.CompanyName))
            .ForMember(d => d.OrganizationId, opt => opt.Ignore())
            .ForMember(d => d.CreatedBy,      opt => opt.Ignore())
            .ForMember(d => d.CreatedDate,    opt => opt.Ignore())
            .ForMember(d => d.IsDelete,       opt => opt.Ignore());
    }
}
