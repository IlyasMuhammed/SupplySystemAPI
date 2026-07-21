import { Routes } from '@angular/router';
import { Empty } from './empty/empty';
import { PrListComponent } from './demand/requisitions/pr-list/pr-list.component';
import { PrCreateComponent } from './demand/requisitions/pr-create/pr-create.component';
import { PrDetailComponent } from './demand/requisitions/pr-detail/pr-detail.component';
import { PrEditComponent } from './demand/requisitions/pr-edit/pr-edit.component';
import { QuotationListComponent } from './demand/quotations/quotation-list/quotation-list.component';
import { QuotationCreateComponent } from './demand/quotations/quotation-create/quotation-create.component';
import { QuotationDetailComponent } from './demand/quotations/quotation-detail/quotation-detail.component';
import { QuotationEditComponent } from './demand/quotations/quotation-edit/quotation-edit.component';
import { PoListComponent } from './demand/purchase-orders/po-list/po-list.component';
import { PoCreateComponent } from './demand/purchase-orders/po-create/po-create.component';
import { PoDetailComponent } from './demand/purchase-orders/po-detail/po-detail.component';
import { PoEditComponent } from './demand/purchase-orders/po-edit/po-edit.component';
import { CustomerComponent } from './customer/customer';
import { SupplierListComponent } from './suppliers/supplier-list/supplier-list.component';
import { SupplierCreateComponent } from './suppliers/supplier-create/supplier-create.component';
import { SupplierDetailComponent } from './suppliers/supplier-detail/supplier-detail.component';
import { SupplierScorecardDashboardComponent } from './suppliers/supplier-scorecard/supplier-scorecard-dashboard.component';
import { CitiesListComponent } from './cities/cities-list/cities-list.component';
import { CitiesCreateComponent } from './cities/cities-create/cities-create.component';
import { CountriesListComponent } from './countries/countries-list/countries-list.component';
import { CountriesCreateComponent } from './countries/countries-create/countries-create.component';
import { LookupValuesListComponent } from './lookup-values/lookup-values-list/lookup-values-list.component';
import { LookupValuesCreateComponent } from './lookup-values/lookup-values-create/lookup-values-create.component';
import { LookupTypesListComponent } from './lookup-types/lookup-types-list/lookup-types-list.component';
import { LookupTypesCreateComponent } from './lookup-types/lookup-types-create/lookup-types-create.component';
import { CurrenciesComponent } from './currencies/currencies.component';
import { PaymentTermsComponent } from './payment-terms/payment-terms.component';
import { permissionGuard } from './gaurds/permission.guard';
import { ProductListComponent } from './inventory/products/product-list/product-list.component';
import { ProductCreateComponent } from './inventory/products/product-create/product-create.component';
import { ProductDetailComponent } from './inventory/products/product-detail/product-detail.component';
import { WarehouseListComponent } from './inventory/warehouses/warehouse-list/warehouse-list.component';
import { WarehouseDetailComponent } from './inventory/warehouses/warehouse-detail/warehouse-detail.component';
import { StockAdjustmentsComponent } from './inventory/stock-adjustments/stock-adjustments.component';
import { ReorderAlertsComponent } from './inventory/reorder-alerts/reorder-alerts.component';
import { CategoryListComponent } from './inventory/categories/category-list.component';
import { ProfileComponent } from './profile/profile.component';
import { GrnListComponent } from './warehouse/grn/grn-list/grn-list.component';
import { GrnCreateComponent } from './warehouse/grn/grn-create/grn-create.component';
import { GrnDetailComponent } from './warehouse/grn/grn-detail/grn-detail.component';
import { GrnEditComponent } from './warehouse/grn/grn-edit/grn-edit.component';
import { SroListComponent } from './warehouse/sro/sro-list/sro-list.component';
import { SroCreateComponent } from './warehouse/sro/sro-create/sro-create.component';
import { SroDetailComponent } from './warehouse/sro/sro-detail/sro-detail.component';
import { CarrierListComponent } from './logistics/carriers/carrier-list/carrier-list.component';
import { CarrierCreateComponent } from './logistics/carriers/carrier-create/carrier-create.component';
import { ShipmentListComponent } from './logistics/shipments/shipment-list/shipment-list.component';
import { ShipmentCreateComponent } from './logistics/shipments/shipment-create/shipment-create.component';
import { ShipmentDetailComponent } from './logistics/shipments/shipment-detail/shipment-detail.component';
import { InvoiceListComponent } from './finance/invoices/invoice-list/invoice-list.component';
import { InvoiceCreateComponent } from './finance/invoices/invoice-create/invoice-create.component';
import { InvoiceDetailComponent } from './finance/invoices/invoice-detail/invoice-detail.component';
import { PaymentListComponent } from './finance/payments/payment-list/payment-list.component';
import { PaymentDetailComponent } from './finance/payments/payment-detail/payment-detail.component';
import { SupplierPaymentListComponent } from './finance/supplier-payments/supplier-payment-list/supplier-payment-list.component';
import { SupplierPaymentCreateComponent } from './finance/supplier-payments/supplier-payment-create/supplier-payment-create.component';
import { SupplierPaymentDetailComponent } from './finance/supplier-payments/supplier-payment-detail/supplier-payment-detail.component';
import { KpiDashboardComponent } from './reports/kpi-dashboard/kpi-dashboard.component';
import { SupplierPerformanceComponent } from './reports/supplier-performance/supplier-performance.component';
import { PoReportsComponent } from './reports/po-reports/po-reports.component';
import { PendingApprovalsComponent } from './reports/pending-approvals/pending-approvals.component';
import { InventoryReportsComponent } from './reports/inventory-reports/inventory-reports.component';
import { GrnVarianceComponent } from './reports/grn-variance/grn-variance.component';
import { ShipmentTrackerComponent } from './reports/shipment-tracker/shipment-tracker.component';
import { FinanceReportsComponent } from './reports/finance-reports/finance-reports.component';
import { AuditTrailComponent } from './reports/audit-trail/audit-trail.component';
import { UserActivityComponent } from './reports/user-activity/user-activity.component';
import { MaterialIssueRegisterComponent } from './reports/material-issue-register/material-issue-register.component';
import { MaterialConsumptionReportsComponent } from './reports/material-consumption-reports/material-consumption-reports.component';
import { StockReportsComponent } from './reports/stock-reports/stock-reports.component';
import { MaterialOpsReportsComponent } from './reports/material-ops-reports/material-ops-reports.component';
import { NotificationsComponent } from './notifications/notifications.component';
import { WorkflowListComponent } from './workflow-admin/workflow-list/workflow-list.component';
import { WorkflowFormComponent } from './workflow-admin/workflow-form/workflow-form.component';
import { ApprovalInboxComponent } from './workflow-inbox/approval-inbox/approval-inbox.component';
import { ApprovalDetailComponent } from './workflow-inbox/approval-detail/approval-detail.component';
import { DocumentHistoryComponent } from './workflow-inbox/document-history/document-history.component';
import { SubCategoryListComponent } from './inventory/sub-categories/sub-category-list.component';
import { RolesComponent } from './admin/roles/roles.component';
import { ProjectListComponent } from './material/projects/project-list/project-list.component';
import { ProjectCreateComponent } from './material/projects/project-create/project-create.component';
import { ProjectDetailComponent } from './material/projects/project-detail/project-detail.component';
import { MirListComponent } from './material/mir/mir-list/mir-list.component';
import { MirCreateComponent } from './material/mir/mir-create/mir-create.component';
import { MirDetailComponent } from './material/mir/mir-detail/mir-detail.component';
import { MivListComponent } from './material/miv/miv-list/miv-list.component';
import { MivCreateComponent } from './material/miv/miv-create/miv-create.component';
import { MivDetailComponent } from './material/miv/miv-detail/miv-detail.component';
import { BatchSerialTraceComponent } from './material/batch-serial-trace/batch-serial-trace.component';
import { DepartmentCostLedgerComponent } from './material/department-cost-ledger/department-cost-ledger.component';
import { ReturnListComponent } from './material/material-return/return-list/return-list.component';
import { ReturnCreateComponent } from './material/material-return/return-create/return-create.component';
import { ReturnDetailComponent } from './material/material-return/return-detail/return-detail.component';
import { WastageListComponent } from './material/wastage/wastage-list/wastage-list.component';
import { WastageDetailComponent } from './material/wastage/wastage-detail/wastage-detail.component';
import { ConsumptionRegisterComponent } from './material/consumption-register/consumption-register.component';

// Permission code constants (mirrors PermissionCodes.cs)
const P = {
  SYSTEM_CONFIGURE:     'SYSTEM_CONFIGURE',
  USER_MANAGE:          'USER_MANAGE',
  AUDIT_LOG_VIEW:       'AUDIT_LOG_VIEW',
  SUPPLIER_VIEW:        'SUPPLIER_VIEW',
  SUPPLIER_CREATE:      'SUPPLIER_CREATE',
  SUPPLIER_EDIT:        'SUPPLIER_EDIT',
  SUPPLIER_MANAGE:      'SUPPLIER_MANAGE',
  RFQ_VIEW:             'RFQ_VIEW',
  RFQ_CREATE:           'RFQ_CREATE',
  RFQ_MANAGE:           'RFQ_MANAGE',
  PO_VIEW:              'PO_VIEW',
  PO_CREATE:            'PO_CREATE',
  PO_EDIT:              'PO_EDIT',
  PO_APPROVE:           'PO_APPROVE',
  REQUISITION_CREATE:   'REQUISITION_CREATE',
  REQUISITION_VIEW_OWN: 'REQUISITION_VIEW_OWN',
  REQUISITION_VIEW_ALL: 'REQUISITION_VIEW_ALL',
  REQUISITION_APPROVE:  'REQUISITION_APPROVE',
  INVENTORY_VIEW:       'INVENTORY_VIEW',
  STOCK_MANAGE:         'STOCK_MANAGE',
  STOCK_ADJUST:         'STOCK_ADJUST',
  REORDER_MANAGE:       'REORDER_MANAGE',
  WAREHOUSE_TRANSFER:   'WAREHOUSE_TRANSFER',
  GOODS_RECEIVE:        'GOODS_RECEIVE',
  GRN_QC_CONFIRM:       'GRN_QC_CONFIRM',
  GRN_APPROVE:          'GRN_APPROVE',
  GRN_FINANCE_APPROVE:  'GRN_FINANCE_APPROVE',
  INVOICE_VIEW:         'INVOICE_VIEW',
  INVOICE_PROCESS:      'INVOICE_PROCESS',
  PAYMENT_VIEW:         'PAYMENT_VIEW',
  PAYMENT_PROCESS:      'PAYMENT_PROCESS',
  PAYMENT_APPROVE:      'PAYMENT_APPROVE',
  DELIVERY_TRACK:       'DELIVERY_TRACK',
  REPORT_VIEW:          'REPORT_VIEW',
  REPORT_EXPORT:        'REPORT_EXPORT',
  WORKFLOW_ADMIN:       'WORKFLOW_ADMIN',
  WORKFLOW_VIEW:        'WORKFLOW_VIEW',
} as const;

export default [
    { path: 'empty', component: Empty },

    // ── Users & Roles (System Admin only) ─────────────────────────────────────
    { path: 'users', component: CustomerComponent,
      canActivate: [permissionGuard(P.USER_MANAGE)] },
    { path: 'user', component: CustomerComponent,
      canActivate: [permissionGuard(P.USER_MANAGE)] },
    { path: 'admin/roles', component: RolesComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },

    // ── Suppliers ─────────────────────────────────────────────────────────────
    { path: 'suppliers/supplier-list', component: SupplierListComponent,
      canActivate: [permissionGuard(P.SUPPLIER_VIEW, P.SUPPLIER_CREATE, P.SUPPLIER_EDIT, P.SUPPLIER_MANAGE)] },
    { path: 'suppliers/supplier-create', component: SupplierCreateComponent,
      canActivate: [permissionGuard(P.SUPPLIER_CREATE, P.SUPPLIER_MANAGE)] },
    { path: 'suppliers/supplier-detail/:uuid', component: SupplierDetailComponent,
      canActivate: [permissionGuard(P.SUPPLIER_VIEW, P.SUPPLIER_EDIT, P.SUPPLIER_MANAGE)] },
    { path: 'suppliers/supplier-scorecard', component: SupplierScorecardDashboardComponent,
      canActivate: [permissionGuard(P.SUPPLIER_VIEW, P.SUPPLIER_EDIT, P.SUPPLIER_MANAGE)] },

    // ── Master Data (System Admin only) ───────────────────────────────────────
    { path: 'cities/cities-list', component: CitiesListComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'cities/cities-create', component: CitiesCreateComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'cities/cities-create/:id', component: CitiesCreateComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'countries', component: CountriesListComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'countries/countries-create', component: CountriesCreateComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'countries/countries-create/:id', component: CountriesCreateComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'lookup-values/lookup-values-list', component: LookupValuesListComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'lookup-values/lookup-values-create', component: LookupValuesCreateComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'lookup-types/lookup-types-list', component: LookupTypesListComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'lookup-types/lookup-types-create', component: LookupTypesCreateComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'currencies', component: CurrenciesComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },
    { path: 'payment-terms', component: PaymentTermsComponent,
      canActivate: [permissionGuard(P.SYSTEM_CONFIGURE)] },

    // ── Inventory — Products ──────────────────────────────────────────────────
    { path: 'inventory/products', component: ProductListComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.STOCK_MANAGE)] },
    { path: 'inventory/products/new', component: ProductCreateComponent,
      canActivate: [permissionGuard(P.STOCK_MANAGE)] },
    { path: 'inventory/products/:id', component: ProductDetailComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.STOCK_MANAGE)] },
    { path: 'inventory/warehouses', component: WarehouseListComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.STOCK_MANAGE)] },
    { path: 'inventory/warehouses/:id', component: WarehouseDetailComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.STOCK_MANAGE)] },
    { path: 'inventory/stock-adjustments', component: StockAdjustmentsComponent,
      canActivate: [permissionGuard(P.STOCK_ADJUST)] },
    { path: 'inventory/reorder-alerts', component: ReorderAlertsComponent,
      canActivate: [permissionGuard(P.REORDER_MANAGE, P.INVENTORY_VIEW)] },
    { path: 'inventory/categories', component: CategoryListComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.STOCK_MANAGE)] },
    { path: 'inventory/sub-categories', component: SubCategoryListComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.STOCK_MANAGE)] },

    // ── Profile (all authenticated users) ────────────────────────────────────
    { path: 'profile', component: ProfileComponent },

    // ── Demand — Requisitions ─────────────────────────────────────────────────
    { path: 'demand/requisitions', component: PrListComponent,
      canActivate: [permissionGuard(P.REQUISITION_VIEW_OWN, P.REQUISITION_VIEW_ALL, P.REQUISITION_CREATE, P.REQUISITION_APPROVE)] },
    { path: 'demand/requisitions/create', component: PrCreateComponent,
      canActivate: [permissionGuard(P.REQUISITION_CREATE)] },
    { path: 'demand/requisitions/:uuid/edit', component: PrEditComponent,
      canActivate: [permissionGuard(P.REQUISITION_CREATE, P.REQUISITION_VIEW_ALL)] },
    { path: 'demand/requisitions/:uuid', component: PrDetailComponent,
      canActivate: [permissionGuard(P.REQUISITION_VIEW_OWN, P.REQUISITION_VIEW_ALL, P.REQUISITION_CREATE, P.REQUISITION_APPROVE)] },

    // ── Demand — Quotations ───────────────────────────────────────────────────
    { path: 'demand/quotations', component: QuotationListComponent,
      canActivate: [permissionGuard(P.RFQ_VIEW, P.RFQ_CREATE, P.RFQ_MANAGE)] },
    { path: 'demand/quotations/create', component: QuotationCreateComponent,
      canActivate: [permissionGuard(P.RFQ_CREATE, P.RFQ_MANAGE)] },
    { path: 'demand/quotations/:uuid/edit', component: QuotationEditComponent,
      canActivate: [permissionGuard(P.RFQ_CREATE, P.RFQ_MANAGE)] },
    { path: 'demand/quotations/:uuid', component: QuotationDetailComponent,
      canActivate: [permissionGuard(P.RFQ_VIEW, P.RFQ_CREATE, P.RFQ_MANAGE)] },

    // ── Demand — Purchase Orders ──────────────────────────────────────────────
    { path: 'demand/purchase-orders', component: PoListComponent,
      canActivate: [permissionGuard(P.PO_VIEW, P.PO_CREATE, P.PO_EDIT, P.PO_APPROVE)] },
    { path: 'demand/purchase-orders/create', component: PoCreateComponent,
      canActivate: [permissionGuard(P.PO_CREATE)] },
    { path: 'demand/purchase-orders/:uuid/edit', component: PoEditComponent,
      canActivate: [permissionGuard(P.PO_EDIT)] },
    { path: 'demand/purchase-orders/:uuid', component: PoDetailComponent,
      canActivate: [permissionGuard(P.PO_VIEW, P.PO_CREATE, P.PO_EDIT, P.PO_APPROVE)] },

    // ── Warehouse — GRN ───────────────────────────────────────────────────────
    { path: 'warehouse/grn', component: GrnListComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE, P.GRN_APPROVE, P.GRN_FINANCE_APPROVE, P.GRN_QC_CONFIRM)] },
    { path: 'warehouse/grn/create', component: GrnCreateComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE)] },
    { path: 'warehouse/grn/:uuid/edit', component: GrnEditComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE, P.GRN_APPROVE)] },
    { path: 'warehouse/grn/:uuid', component: GrnDetailComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE, P.GRN_APPROVE, P.GRN_FINANCE_APPROVE, P.GRN_QC_CONFIRM)] },

    // ── Warehouse — SRO ───────────────────────────────────────────────────────
    { path: 'warehouse/sro', component: SroListComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE, P.WAREHOUSE_TRANSFER)] },
    { path: 'warehouse/sro/create', component: SroCreateComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE, P.WAREHOUSE_TRANSFER)] },
    { path: 'warehouse/sro/:uuid', component: SroDetailComponent,
      canActivate: [permissionGuard(P.GOODS_RECEIVE, P.WAREHOUSE_TRANSFER)] },

    // ── Logistics — Carriers ──────────────────────────────────────────────────
    { path: 'logistics/carriers', component: CarrierListComponent,
      canActivate: [permissionGuard(P.DELIVERY_TRACK)] },
    { path: 'logistics/carriers/create', component: CarrierCreateComponent,
      canActivate: [permissionGuard(P.DELIVERY_TRACK)] },

    // ── Logistics — Shipments ─────────────────────────────────────────────────
    { path: 'logistics/shipments', component: ShipmentListComponent,
      canActivate: [permissionGuard(P.DELIVERY_TRACK)] },
    { path: 'logistics/shipments/create', component: ShipmentCreateComponent,
      canActivate: [permissionGuard(P.DELIVERY_TRACK)] },
    { path: 'logistics/shipments/:uuid', component: ShipmentDetailComponent,
      canActivate: [permissionGuard(P.DELIVERY_TRACK)] },

    // ── Finance — Invoices ────────────────────────────────────────────────────
    { path: 'finance/invoices', component: InvoiceListComponent,
      canActivate: [permissionGuard(P.INVOICE_VIEW, P.INVOICE_PROCESS)] },
    { path: 'finance/invoices/create', component: InvoiceCreateComponent,
      canActivate: [permissionGuard(P.INVOICE_PROCESS)] },
    { path: 'finance/invoices/:uuid', component: InvoiceDetailComponent,
      canActivate: [permissionGuard(P.INVOICE_VIEW, P.INVOICE_PROCESS)] },

    // ── Finance — Payments (multi-invoice allocation, DRAFT→APPROVED→POSTED) ───
    { path: 'finance/payments', component: SupplierPaymentListComponent,
      canActivate: [permissionGuard(P.PAYMENT_VIEW, P.PAYMENT_PROCESS)] },
    { path: 'finance/payments/create', component: SupplierPaymentCreateComponent,
      canActivate: [permissionGuard(P.PAYMENT_PROCESS)] },
    { path: 'finance/payments/:uuid', component: SupplierPaymentDetailComponent,
      canActivate: [permissionGuard(P.PAYMENT_VIEW, P.PAYMENT_PROCESS)] },

    // ── Finance — Payments (legacy, read-only history) ──────────────────────────
    { path: 'finance/payments-legacy', component: PaymentListComponent,
      canActivate: [permissionGuard(P.PAYMENT_VIEW, P.PAYMENT_PROCESS)] },
    { path: 'finance/payments-legacy/:uuid', component: PaymentDetailComponent,
      canActivate: [permissionGuard(P.PAYMENT_VIEW, P.PAYMENT_PROCESS)] },

    // ── Reports & Analytics ───────────────────────────────────────────────────
    { path: 'reports/kpi-dashboard', component: KpiDashboardComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/supplier-performance', component: SupplierPerformanceComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/po-reports', component: PoReportsComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/pending-approvals', component: PendingApprovalsComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/inventory-reports', component: InventoryReportsComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/grn-variance', component: GrnVarianceComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/shipment-tracker', component: ShipmentTrackerComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/finance-reports', component: FinanceReportsComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW, P.REPORT_EXPORT)] },
    { path: 'reports/audit-trail', component: AuditTrailComponent,
      canActivate: [permissionGuard(P.AUDIT_LOG_VIEW, P.REPORT_VIEW)] },
    { path: 'reports/user-activity', component: UserActivityComponent,
      canActivate: [permissionGuard(P.AUDIT_LOG_VIEW, P.REPORT_VIEW)] },

    // ── Material Reports ──────────────────────────────────────────────────────
    { path: 'reports/material-issue-register', component: MaterialIssueRegisterComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW)] },
    { path: 'reports/material-consumption-reports', component: MaterialConsumptionReportsComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW)] },
    { path: 'reports/stock-reports', component: StockReportsComponent,
      canActivate: [permissionGuard(P.INVENTORY_VIEW, P.REPORT_VIEW)] },
    { path: 'reports/material-ops-reports', component: MaterialOpsReportsComponent,
      canActivate: [permissionGuard(P.REPORT_VIEW)] },

    // ── Notifications (all authenticated users) ───────────────────────────────
    { path: 'notifications', component: NotificationsComponent },

    // ── Workflow Admin ────────────────────────────────────────────────────────
    { path: 'workflow-admin/workflows', component: WorkflowListComponent,
      canActivate: [permissionGuard(P.WORKFLOW_ADMIN)] },
    { path: 'workflow-admin/workflows/new', component: WorkflowFormComponent,
      canActivate: [permissionGuard(P.WORKFLOW_ADMIN)] },
    { path: 'workflow-admin/workflows/:id/edit', component: WorkflowFormComponent,
      canActivate: [permissionGuard(P.WORKFLOW_ADMIN)] },

    // ── Approval Inbox (all authenticated users) ──────────────────────────────
    { path: 'workflow-inbox',        component: ApprovalInboxComponent },
    { path: 'workflow-inbox/:uuid',  component: ApprovalDetailComponent },
    { path: 'documents/:id/history', component: DocumentHistoryComponent },

    // ── Material — Projects ───────────────────────────────────────────────────
    { path: 'material/projects',       component: ProjectListComponent },
    { path: 'material/projects/new',   component: ProjectCreateComponent },
    { path: 'material/projects/:uuid/edit', component: ProjectCreateComponent },
    { path: 'material/projects/:uuid', component: ProjectDetailComponent },

    // ── Material — Material Issue Requests ────────────────────────────────────
    { path: 'material/mir',        component: MirListComponent },
    { path: 'material/mir/create', component: MirCreateComponent },
    { path: 'material/mir/:uuid',  component: MirDetailComponent },

    // ── Material — Material Issue Vouchers ────────────────────────────────────
    { path: 'material/miv',        component: MivListComponent },
    { path: 'material/miv/create', component: MivCreateComponent },
    { path: 'material/miv/:uuid',  component: MivDetailComponent },

    // ── Material — Batch / Serial Trace ───────────────────────────────────────
    { path: 'material/batch-serial/trace', component: BatchSerialTraceComponent },

    // ── Material — Department Cost Ledger ─────────────────────────────────────
    { path: 'material/cost-ledger/departments', component: DepartmentCostLedgerComponent },

    // ── Material — Returns ────────────────────────────────────────────────────
    { path: 'material/returns',        component: ReturnListComponent },
    { path: 'material/returns/create', component: ReturnCreateComponent },
    { path: 'material/returns/:uuid',  component: ReturnDetailComponent },

    // ── Material — Wastage ────────────────────────────────────────────────────
    { path: 'material/wastage',       component: WastageListComponent },
    { path: 'material/wastage/:uuid', component: WastageDetailComponent },

    // ── Material — Consumption Register ──────────────────────────────────────
    { path: 'material/consumption-register', component: ConsumptionRegisterComponent },

    { path: '**', redirectTo: '/notfound' }
] as Routes;
