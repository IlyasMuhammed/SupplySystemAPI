import { ApiResponse as SetupResponse, ExchangeRateModel, ExchangeRateQuoteModel, SaveExchangeRateRequest, SaveTaxCodeRequest, TaxCodeModel, TaxCodesFromRatesResult } from './finance-setup.service';
import { ApiResponse as FinanceResponse, CreateInvoiceRequest, InvoiceDetailModel, InvoiceListItemModel, PatchInvoiceRequest, ReverseInvoiceRequest } from './finance.service';
import { ApiResponse as LogisticsResponse } from './logistics.service';
import { ApiResponse as CoreResponse } from './api.service';
import { SalesInvoiceDetailModel, SalesInvoiceLineModel } from './sales-invoice.service';
import { SaleOrderLineModel, SaleOrderLineRequest } from './sale-order.service';
import { TaxCodeMappingItem, TaxCodeMappingModel } from '../models/quickbooks-integration.models';

/**
 * Cross-page consistency (SAP alignment): the TypeScript shapes carry every field the backend sends for the
 * new features, under the camelCase name System.Text.Json gives it. Each list below is the C# model's
 * properties (SMS.Modules.Finance/Models, SMS.Modules.Demand/Models/SaleOrderModels.cs,
 * SMS.Modules.Integration/Models/AdminModels.cs), checked by the compiler: a renamed or dropped TS field
 * stops this file compiling. The runtime assertions only make the lists visible in the test report.
 */

/** A field list the compiler holds to the interface: every name must be a key of T. */
const fields = <T>() => <K extends keyof T>(...keys: K[]) => keys;

/** True only when A and B accept exactly the same values. */
type Same<A, B> = [A] extends [B] ? ([B] extends [A] ? true : false) : false;

describe('SAP alignment consistency: frontend models match the backend contract', () => {

  it('tax codes and exchange rates (FinanceSetupModels.cs)', () => {
    expect(fields<TaxCodeModel>()('uuid', 'code', 'name', 'description', 'ratePercent', 'usage', 'isDefault', 'isActive').length).toBe(8);
    expect(fields<SaveTaxCodeRequest>()('code', 'name', 'description', 'ratePercent', 'usage', 'isDefault', 'isActive').length).toBe(7);
    expect(fields<TaxCodesFromRatesResult>()('created', 'skippedRates').length).toBe(2);
    expect(fields<ExchangeRateModel>()('uuid', 'fromCurrencyCode', 'toCurrencyCode', 'rate', 'effectiveDate', 'source', 'notes', 'createdDate').length).toBe(8);
    expect(fields<SaveExchangeRateRequest>()('fromCurrencyCode', 'toCurrencyCode', 'rate', 'effectiveDate', 'notes').length).toBe(5);
    expect(fields<ExchangeRateQuoteModel>()('fromCurrencyCode', 'toCurrencyCode', 'rate', 'effectiveDate', 'inverted').length).toBe(5);
  });

  it('sale order lines (SaleOrderModels.cs): taxCodeUuid and taxCode', () => {
    expect(fields<SaleOrderLineModel>()('taxPercent', 'taxCodeUuid', 'taxCode').length).toBe(3);
    expect(fields<SaleOrderLineRequest>()('variantUuid', 'quantity', 'discountPercent', 'taxPercent', 'taxCodeUuid').length).toBe(5);
  });

  it('sales invoices (ReceivablesModels.cs): line code snapshot, currency snapshot, cancellation', () => {
    expect(fields<SalesInvoiceLineModel>()('taxPercent', 'taxCodeUuid', 'taxCode').length).toBe(3);
    expect(fields<SalesInvoiceDetailModel>()(
      'exchangeRate', 'baseCurrencyCode', 'baseGrandTotal', 'cancelledAt', 'cancelledBy', 'cancellationReason').length).toBe(6);
  });

  it('supplier invoices (FinanceModels.cs): tax code, currency snapshot, reversal', () => {
    expect(fields<InvoiceDetailModel>()(
      'taxCodeUuid', 'taxCode', 'taxPercent', 'exchangeRate', 'baseCurrencyCode', 'baseTotalAmount',
      'reversedAt', 'reversedBy', 'reversalReason', 'matchStatus').length).toBe(10);
    expect(fields<InvoiceListItemModel>()('taxCode', 'taxPercent', 'matchStatus').length).toBe(3);
    expect(fields<CreateInvoiceRequest>()('taxAmount', 'taxCodeUuid').length).toBe(2);
    expect(fields<PatchInvoiceRequest>()('taxAmount', 'taxCodeUuid', 'matchStatus', 'notes').length).toBe(4);
    expect(fields<ReverseInvoiceRequest>()('reason').length).toBe(1);
  });

  it('QuickBooks tax mappings (AdminModels.cs): code rows, their SMS name/usage, and suggestions', () => {
    expect(fields<TaxCodeMappingModel>()(
      'sourceTaxCode', 'sourceTaxCodeName', 'sourceTaxCodeUsage', 'taxPercent', 'qboTaxCodeId', 'qboTaxCodeName', 'timesSeen',
      'suggestedQboTaxCodeId', 'suggestedQboTaxCodeName').length).toBe(9);
    expect(fields<TaxCodeMappingItem>()('sourceTaxCode', 'taxPercent', 'qboTaxCodeId').length).toBe(3);
  });

  it('the ApiResponse envelopes these pages use have not drifted apart', () => {
    const setupVsFinance: Same<SetupResponse<number>, FinanceResponse<number>> = true;
    const setupVsLogistics: Same<SetupResponse<number>, LogisticsResponse<number>> = true;
    const setupVsCore: Same<SetupResponse<number>, CoreResponse<number>> = true;
    const defaults: Same<SetupResponse, FinanceResponse> = true;
    expect([setupVsFinance, setupVsLogistics, setupVsCore, defaults]).toEqual([true, true, true, true]);
  });
});
