using DesignMode.AI.Shared.Models.MarketingReporting;

namespace DesignMode.AI.Server.Services;

/// <summary>Pure reporting calculations. Inputs are read-only source facts and no provider is called.</summary>
public static class MarketingFinancialCalculator
{
    private const int MoneyDigits = 2;
    private const int RatioDigits = 4;

    public static MarketingFinancialMeasures Calculate(MarketingReportingPeriod period,
        IReadOnlyList<PurchaseRevenueFact> purchases, IReadOnlyList<GoogleAdsDailySpend> spend,
        ReportingSourceCoverage purchaseCoverage, ReportingSourceCoverage adsCoverage,
        decimal? variableCostsExcludingAds = null, ReportingDataState variableCostState = ReportingDataState.Unavailable,
        decimal? contributionPerCustomerPerMonth = null, ReportingDataState paybackBasisState = ReportingDataState.Unavailable)
    {
        var measures = new MarketingFinancialMeasures();
        var validPurchases = purchases.Where(x => x.PaidAt >= period.StartInclusive && x.PaidAt < period.EndExclusive).ToArray();
        var usdPurchases = validPurchases.Where(x => x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var usdSpend = spend.Where(x => x.Date >= period.StartDate && x.Date <= period.EndDate && x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var revenueState = Combine(purchaseCoverage.State, ReportingDataState.Partial);
        var spendState = Combine(adsCoverage.State, ReportingDataState.Partial);
        var revenue = usdPurchases.Sum(x => x.Amount);
        var adSpend = usdSpend.Sum(x => x.Amount);
        var eligibleCustomers = usdPurchases.Select(x => x.CustomerKey).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count();
        var newCustomers = usdPurchases.Where(x => x.RevenueKind == PurchaseRevenueKind.SubscriptionRevenue && x.IsFirstPayment && x.HasGoogleTransactionAttribution)
            .Select(x => x.CustomerKey).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count();
        var customerState = usdPurchases.Any(x => string.IsNullOrWhiteSpace(x.CustomerKey)) ? ReportingDataState.Partial : revenueState;

        measures = measures with
        {
            AdvertisingSpend = Measure(RoundMoney(adSpend), spendState, "USD Ads account spend; campaign-level split is not currently supplied."),
            GoogleAdsSpend = Measure(RoundMoney(adSpend), spendState, "Google Ads daily account-level spend."),
            EligibleRevenue = Measure(RoundMoney(revenue), revenueState, "Eligible positive paid purchase facts; adjustment and historical coverage remains partial."),
            TotalCollectedRevenue = Measure(RoundMoney(revenue), revenueState, "Positive purchase facts only; refunds, chargebacks and fees are not included."),
            SubscriptionRevenue = Measure(RoundMoney(usdPurchases.Where(x => x.RevenueKind == PurchaseRevenueKind.SubscriptionRevenue).Sum(x => x.Amount)), revenueState, "Eligible subscription payments."),
            AICreditRevenue = Measure(RoundMoney(usdPurchases.Where(x => x.RevenueKind == PurchaseRevenueKind.AICreditRevenue).Sum(x => x.Amount)), revenueState, "Eligible non-test AI-credit payments."),
            GoogleAttributedRevenue = Measure(RoundMoney(usdPurchases.Where(x => x.HasGoogleTransactionAttribution).Sum(x => x.Amount)), revenueState, "Transaction snapshot evidence only; attribution coverage is partial."),
            EligibleCustomers = new ReportingCount { Count = eligibleCustomers, State = customerState, Explanation = "Distinct customer keys with eligible USD paid transactions in the selected period." },
            NewCustomers = new ReportingCount { Count = newCustomers, State = customerState, Explanation = "Distinct customers with an attributed initial subscription payment in the selected period; not all-channel acquisition." },
            AverageRevenuePerCustomer = eligibleCustomers > 0 ? Measure(RoundMoney(revenue / eligibleCustomers), customerState, "Eligible USD revenue divided by distinct eligible paying customers in this period.") : Missing("No eligible customer denominator."),
            ReturnOnAdSpend = adSpend > 0 ? Measure(RoundRatio(usdPurchases.Where(x => x.HasGoogleTransactionAttribution).Sum(x => x.Amount) / adSpend), Combine(revenueState, spendState), "Transaction-attributed USD revenue divided by USD Ads spend.") : Missing("ROAS requires positive comparable USD Ads spend."),
            CustomerAcquisitionCost = adSpend > 0 && newCustomers > 0 ? Measure(RoundMoney(adSpend / newCustomers), Combine(spendState, customerState), "USD account spend divided by distinct attributed first-payment customers; source is not campaign matched.") : Missing("CAC requires positive USD Ads spend and at least one distinct attributed new customer."),
            ContributionBeforeAds = variableCostsExcludingAds is not null ? Measure(RoundMoney(revenue - variableCostsExcludingAds.Value), Combine(revenueState, variableCostState), "Revenue less explicitly supplied variable costs excluding ads.") : Missing("Variable costs excluding advertising are not currently classified by source."),
            ContributionAfterAds = variableCostsExcludingAds is not null ? Measure(RoundMoney(revenue - variableCostsExcludingAds.Value - adSpend), Combine(Combine(revenueState, variableCostState), spendState), "Contribution before Ads less Ads spend.") : Missing("Unavailable until variable costs excluding advertising have supported coverage."),
            ContributionPerCustomer = variableCostsExcludingAds is not null && eligibleCustomers > 0 ? Measure(RoundMoney((revenue - variableCostsExcludingAds.Value - adSpend) / eligibleCustomers), customerState, "Contribution after Ads divided by eligible customers.") : Missing("Requires supported variable costs and eligible customers."),
            CustomerAcquisitionPaybackMonths = contributionPerCustomerPerMonth is > 0 && newCustomers > 0 && paybackBasisState is ReportingDataState.Complete or ReportingDataState.Partial
                ? Measure(RoundRatio((adSpend / newCustomers) / contributionPerCustomerPerMonth.Value), paybackBasisState, "CAC divided by explicitly supplied monthly contribution per customer; not inferred from recurring revenue.")
                : Missing("Payback needs an explicit positive monthly contribution-per-customer basis and eligible new customers."),
            NonUsdAmounts = validPurchases.Where(x => !x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase).Select(g => new CurrencyAmount { Currency = g.Key, Amount = RoundMoney(g.Sum(x => x.Amount)), RecordCount = g.LongCount() }).ToArray()
        };
        return measures;
    }

    public static MarginalPerformanceReport CalculateMarginal(MarketingFinancialMeasures selected, MarketingFinancialMeasures comparison)
    {
        var spendDelta = Difference(selected.AdvertisingSpend, comparison.AdvertisingSpend);
        var revenueDelta = Difference(selected.GoogleAttributedRevenue, comparison.GoogleAttributedRevenue);
        var customerDelta = Difference(selected.NewCustomers, comparison.NewCustomers);
        var marginalRoas = spendDelta.Amount > 0 && revenueDelta.Amount is not null
            ? Measure(RoundRatio(revenueDelta.Amount.Value / spendDelta.Amount!.Value), Combine(spendDelta.State, revenueDelta.State), "Observed period-over-period attributed revenue change divided by Ads spend change; not causal.")
            : Missing("Marginal ROAS requires positive spend change and comparable revenue coverage.");
        var marginalCac = customerDelta.Count > 0 && spendDelta.Amount > 0
            ? Measure(RoundMoney(spendDelta.Amount!.Value / customerDelta.Count!.Value), Combine(spendDelta.State, customerDelta.State), "Observed spend change divided by new-customer change; not causal.")
            : Missing("Marginal CAC requires positive spend and new-customer changes.");
        return new MarginalPerformanceReport { AdvertisingSpendChange = spendDelta, AttributedRevenueChange = revenueDelta, NewCustomersChange = customerDelta,
            MarginalRoas = marginalRoas, MarginalCac = marginalCac,
            IncrementalContribution = Missing("Variable costs are not available at the same period and campaign basis."),
            State = marginalRoas.State == ReportingDataState.Unavailable && marginalCac.State == ReportingDataState.Unavailable ? ReportingDataState.Unavailable : ReportingDataState.Partial,
            Explanation = "Period deltas are observational comparisons and do not establish that advertising caused the change." };
    }

    public static CampaignFinancialReport CalculateCampaign(CampaignFinancialInput input, CampaignRecommendationPolicy? policy)
    {
        var limitations = new List<string>();
        var state = input.State;
        var spend = input.AdvertisingSpend;
        var revenue = input.AttributedRevenue;
        var roas = spend > 0 && revenue is not null ? Measure(RoundRatio(revenue.Value / spend.Value), state, "Campaign attributed revenue / campaign spend.") : Missing("Campaign ROAS requires positive spend and supported attributed revenue.");
        var cac = spend is not null && input.NewCustomers is > 0 ? Measure(RoundMoney(spend.Value / input.NewCustomers.Value), state, "Campaign spend / campaign new customers.") : Missing("Campaign CAC requires spend and positive customer count.");
        var contribution = input.VariableCostsExcludingAds is not null && revenue is not null && spend is not null
            ? Measure(RoundMoney(revenue.Value - input.VariableCostsExcludingAds.Value - spend.Value), state, "Attributed revenue less supported campaign variable costs and spend.")
            : Missing("Campaign contribution needs supported variable costs excluding advertising.");
        var action = CampaignRecommendationAction.Unavailable;
        var rationale = "A complete target policy and campaign evidence are required.";
        if (policy is not null && policy.TargetRoas is > 0 && policy.MinimumRoas is >= 0 && input.NewCustomers >= policy.MinimumNewCustomers && roas.Amount is not null &&
            (policy.TargetCac is null or <= 0 || cac.Amount is not null) && state is ReportingDataState.Complete or ReportingDataState.Partial)
        {
            if (policy.TargetCac is > 0 && cac.Amount is not null && cac.Amount.Value > policy.TargetCac.Value)
            { action = CampaignRecommendationAction.Reduce; rationale = $"CAC {cac.Amount:F2} exceeds configured target {policy.TargetCac:F2}; advisory only."; }
            else if (roas.Amount >= policy.TargetRoas) { action = CampaignRecommendationAction.Scale; rationale = $"ROAS {roas.Amount:F4} meets configured target {policy.TargetRoas:F4}; advisory only."; }
            else if (roas.Amount < policy.MinimumRoas) { action = CampaignRecommendationAction.Pause; rationale = $"ROAS {roas.Amount:F4} is below configured minimum {policy.MinimumRoas:F4}; advisory only."; }
            else { action = CampaignRecommendationAction.Hold; rationale = $"ROAS {roas.Amount:F4} is between configured minimum and target; advisory only."; }
        }
        else limitations.Add("Campaign source data, configured thresholds, or minimum customer evidence is missing.");
        limitations.Add("Recommendations are observational guidance; this calculator never changes Ads settings.");
        return new CampaignFinancialReport { CampaignId = input.CampaignId, CampaignName = input.CampaignName,
            AdvertisingSpend = spend is null ? Missing("Campaign spend unavailable.") : Measure(RoundMoney(spend.Value), state, null),
            AttributedRevenue = revenue is null ? Missing("Campaign attributed revenue unavailable.") : Measure(RoundMoney(revenue.Value), state, null),
            Roas = roas, CustomerAcquisitionCost = cac, ContributionAfterAds = contribution,
            NewCustomers = new ReportingCount { Count = input.NewCustomers, State = input.NewCustomers is null ? ReportingDataState.Unavailable : state },
            Recommendation = action, RecommendationRationale = rationale, Limitations = limitations };
    }

    /// <summary>Profit only when adjustment and expense coverage is explicitly supplied.</summary>
    public static (ReportingMeasure BusinessExpenses, ReportingMeasure OverallBusinessProfit) CalculateBusinessProfit(
        ReportingMeasure totalCollectedRevenue, IReadOnlyList<DesignMode.AI.Shared.Models.BusinessExpense> expenses,
        ReportingMeasure advertisingSpend, decimal? refundsAndChargebacks, decimal? stripeFees,
        ReportingDataState expenseCoverage, ReportingDataState adjustmentsCoverage)
    {
        var usd = expenses.Where(x => x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var hasNonUsdRows = expenses.Any(x => !x.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase));
        var adCategoryRows = usd.Where(x => x.Category.Equals("advertising/marketing", StringComparison.OrdinalIgnoreCase)).ToArray();
        var supportedExpenses = usd.Where(x => !x.Category.Equals("advertising/marketing", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Amount);
        var expenseState = adCategoryRows.Length > 0 || hasNonUsdRows ? Combine(expenseCoverage, ReportingDataState.Partial) : expenseCoverage;
        var expensesMeasure = Measure(RoundMoney(supportedExpenses), expenseState,
            adCategoryRows.Length > 0 ? "Advertising/marketing expense rows excluded to prevent overlap with Ads spend; reconciliation is required." :
            hasNonUsdRows ? "USD approved expense facts only; non-USD expense rows are retained separately and not converted." : "USD approved expense facts, excluding advertising/marketing category.");

        // Category-level advertising expenses cannot be safely reconciled to account-level Ads spend.
        if (totalCollectedRevenue.Amount is null || advertisingSpend.Amount is null || refundsAndChargebacks is null || stripeFees is null ||
            adCategoryRows.Length > 0 || expenseCoverage is ReportingDataState.Unavailable or ReportingDataState.Error ||
            adjustmentsCoverage is ReportingDataState.Unavailable or ReportingDataState.Error)
            return (expensesMeasure, Missing("Overall business profit requires supported revenue, Ads spend, adjustment coverage and reconciled approved expenses."));

        var state = Combine(Combine(totalCollectedRevenue.State, advertisingSpend.State), Combine(expenseState, adjustmentsCoverage));
        var profit = totalCollectedRevenue.Amount.Value - refundsAndChargebacks.Value - stripeFees.Value - advertisingSpend.Amount.Value - supportedExpenses;
        return (expensesMeasure, Measure(RoundMoney(profit), state,
            "Total collected revenue less refunds/chargebacks, Stripe fees, Ads spend, and supported approved expenses."));
    }

    public static (ReportingMeasure MarketingProfit, ReportingMeasure MarketingProfitMarginPercent) CalculateMarketingProfit(
        ReportingMeasure attributedRevenue, ReportingMeasure advertisingSpend, decimal? associatedRefundsAndChargebacks,
        decimal? associatedStripeFees, ReportingDataState deductionAssociationCoverage)
    {
        if (attributedRevenue.Amount is null || advertisingSpend.Amount is null || attributedRevenue.Amount <= 0 ||
            associatedRefundsAndChargebacks is null || associatedStripeFees is null ||
            deductionAssociationCoverage is ReportingDataState.Unavailable or ReportingDataState.Error)
            return (Missing("Marketing profit requires attributed revenue, Ads spend, and reliably associated supported deductions."),
                Missing("Marketing margin requires supported marketing profit and positive attributed revenue."));

        var state = Combine(Combine(attributedRevenue.State, advertisingSpend.State), deductionAssociationCoverage);
        var profit = attributedRevenue.Amount.Value - associatedRefundsAndChargebacks.Value - associatedStripeFees.Value - advertisingSpend.Amount.Value;
        var margin = profit / attributedRevenue.Amount.Value * 100m;
        return (Measure(RoundMoney(profit), state, "Attributed revenue less associated refunds/chargebacks, Stripe fees, and Ads spend."),
            Measure(RoundRatio(margin), state, "Marketing profit divided by attributed revenue, multiplied by 100."));
    }

    private static ReportingMeasure Difference(ReportingMeasure a, ReportingMeasure b) => a.Amount is not null && b.Amount is not null
        ? Measure(RoundMoney(a.Amount.Value - b.Amount.Value), Combine(a.State, b.State), "Selected minus comparison period.") : Missing("Comparable period measure unavailable.");
    private static ReportingCount Difference(ReportingCount a, ReportingCount b) => a.Count is not null && b.Count is not null
        ? new ReportingCount { Count = a.Count.Value - b.Count.Value, State = Combine(a.State, b.State), Explanation = "Selected minus comparison period; observational." }
        : new ReportingCount { State = ReportingDataState.Unavailable, Explanation = "Comparable period count unavailable." };
    private static ReportingMeasure Measure(decimal value, ReportingDataState state, string? reason) => new() { Amount = value, Currency = "USD", State = state, Explanation = reason };
    private static ReportingMeasure Missing(string reason) => new() { Amount = null, Currency = "USD", State = ReportingDataState.Unavailable, Explanation = reason };
    private static decimal RoundMoney(decimal value) => decimal.Round(value, MoneyDigits, MidpointRounding.AwayFromZero);
    private static decimal RoundRatio(decimal value) => decimal.Round(value, RatioDigits, MidpointRounding.AwayFromZero);
    private static ReportingDataState Combine(ReportingDataState a, ReportingDataState b)
    {
        if (a == ReportingDataState.Error || b == ReportingDataState.Error) return ReportingDataState.Error;
        if (a == ReportingDataState.Unavailable || b == ReportingDataState.Unavailable) return ReportingDataState.Unavailable;
        if (a == ReportingDataState.Stale || b == ReportingDataState.Stale) return ReportingDataState.Stale;
        return a == ReportingDataState.Partial || b == ReportingDataState.Partial ? ReportingDataState.Partial : ReportingDataState.Complete;
    }
}
