# VoxLadder — Final Checkpoint (Verified)

> **Verification:** Parameters confirmed against source code, `VoxLadder_Final_Parameters_V2.csv`, and a clean backtest run matched against `TradeRecords_TestB_SkipEntry.csv` on 2026-03-08.

---

## What VoxLadder Is

NIFTY index options strategy: credit spread + directional buy legs + near-expiry gamma plays. 30-min custom bars, rule-based signals (EMA/RSI/VIX/ADX/Volume/OBV), positional (4–9 DTE spreads).

Capital: ₹1,85,000 · Backtest: Jan 2021 – Feb 2026

---

## Architecture: Original → Final

### V1 — Coupled 3-Leg (Original)

```
TradeRecord = [SellLeg + BuyLeg + FarLeg]  — all coupled, single exit
```

Problems:
- Spread SL contaminated by far leg PnL (spread + far leg summed into `CurrentPnL`)
- Far leg killed when spread hit SL, even if directional move was developing
- `IsPartiallyBooked` flag created branching SL logic — fragile, hard to optimise
- No independent lifecycle for far leg

### V2 — Decoupled 3-Component (Final)

```
SpreadTrade    = [SellLeg + BuyLeg]     — credit spread, own SL/SwingExit
LadderTrade    = [FarLeg]               — independent directional buy, own PB/SL
GammaBurst     = [NearExpiryLeg]        — near-expiry gamma play, DTE < 2d
```

What changed:
- Each component has independent entry, exit, parameter set
- Universal TradeRecord: max 2 OptionInfo (SellLeg + BuyLeg), SellLeg=null for buy-only trades
- TradeType enum: BullLadder, BearLadder, BullLadderTrade, BearLadderTrade, BullGammaBurst, BearGammaBurst
- LadderTrade and GammaBurst require zero additional margin (premium-paid)
- Spread SL simplified: always `CurrentPnL < -SLF × credit` (no IsPartiallyBooked branch)
- `SpreadPnL` now equals `CurrentPnL` (no far leg in the trade)
- `IsPartiallyBooked` hardcoded to `false`

---

## Signal Logic (Unchanged V1 → V2)

All three components fire on the same signal. Signal is NOT optimised — only trade construction and exits are.

### Indicators (StrategyParameters.cs)
- EMA Fast/Slow: 3 / 10
- RSI: period 7
- VIX EMA Fast/Slow: 5 / 20
- Volume SMA: 20
- OBV EMA Fast/Slow: 10 / 20
- ADX Custom: period 14, ADX Daily: period 7

### Bull Trigger (SignalRuleBased.cs)
Any of:
- Full indicator confluence: EMA bull (gap > 0.0%) AND RSI > 45 AND VIX declining AND volume > 1.2× avg
- EMA gap overpower: gap > 0.4%
- Up swing: spot moved > 2% from reference (or > 1% if already in bull)

### Bear Trigger
Symmetric with RSI < 35, EMA gap < -0.4%, down swing < -2%. Bear divergence (OBV diverging while other indicators bull, running signal count ≥ 4). Bull always preferred on conflict.

### Trade Guard (SetCustomTradeType)
ADX + VIX tiered position limits:
- ADX combined = |normAdxCustom + normAdxDaily × 0.67|
- ADX contrib: < 3.70 → -2, < 4.60 → -1, < 5.80 → 0, else → +1
- VIX contrib: > 25 → 0, > 19 → +1, > 16 → +2, else → +3
- Max open per direction = vixContrib + adxContrib
- Also requires: ADX direction aligned with signal, EMA gap > 0.05%, pyramid condition met

---

## Component 1: Credit Spread

### What It Does
Sells OTM option at target premium, buys further OTM as hedge. Collects theta. Exits on SL (credit-based) or swing reversal.

### Parameter Changes (V1 → V2)

| Parameter | V1 | V2 | Why |
|-----------|----|----|-----|
| StopLossFactor | 1.25 | 0.75 | Tighter SL — cut losers faster |
| SwingExitPct | 1.50 | 1.25 | Tighter reversal exit — exit sooner on adverse swing |
| TargetPOP | 85 | 90 | Higher POP — sell further OTM, safer strikes |
| BuyPremiumRatio | 0.50 | 0.30 | Cheaper hedge — buy leg costs 30% of sell premium |
| TargetMaxLossPct | 0.25 | 0.25 | Unchanged — caps max loss to 25% of margin |
| EnableTargetMaxLossAdj | true | true | Active — narrows spread if max loss exceeds cap |
| MinSellPremium | — | 20.0 | Adjusts sell strike to meet minimum credit |
| MinBuyPremium | — | 5.0 | Adjusts buy strike to meet minimum hedge value |
| MaxSpreadWidth | — | 1000 | Narrows spread if width exceeds cap |
| MaxConcurrentSpreads | — | 3 | Max open spread positions at any time |
| ForceCloseOldestOnOverflow | — | false | Skip entry when at limit (don't force-close) |

### Unchanged Spread Params
- TargetSellPremium = 15
- MinSpreadWidth = 100
- SpreadWidthBuyAdjBuffer = 50
- CreditUpperBound = 2000
- MinDaysToExpiry = 4
- SpreadLotsPerTrade = 1
- ExpiryDayMarginMult = 1.3
- ExpiryMarginScale = 1.30

### Spread Exit Logic (TradeMonitorAndExits.cs)
```
SL:    CurrentPnL < -SLF × min(credit, CreditUpperBound × SpreadLotsPerTrade)
Swing: |MostFavSpot - CurrentSpot| / MostFavSpot × 100 > SwingExitPct
```

---

## Component 2: LadderTrade (Far Leg)

### What It Does
Buys a directional option in the spread's expiry (LadderExpiryDT, 4–9 DTE). Independent lifecycle — survives spread exit. Captures trending moves.

### V1 Behaviour (Coupled)
- Far leg was third leg of TradeRecord
- Killed when spread hit SL
- No independent SL or profit booking
- BullLadderTrade returned ₹105 total (essentially breakeven)

### V2 Parameters (Final — BalancedLow Config)

| Parameter | Value |
|-----------|-------|
| EnableLadderTrade | true |
| TargetLadderTradePremium | 15.0 |
| LadderTradePBMult | 3.0 |
| LadderTradePBAdd | 25.0 |
| LadderTradeSLPct | 20 |
| LadderTradeSpotThreshold | 1.50 |

### LadderTrade Exit Logic (MonitorLadderTrades)
```
PB:  currentPremium > entryPremium × PBMult + PBAdd  AND  spotMove > SpotThreshold%
SL:  currentPremium < entryPremium × (1 - SLPct/100)
```

### Why BalancedLow, Not Aggressive
The standalone Optuna winner was Aggressive (Prem=50, PB=4.0×, SL=20, Spot=2.0) with ₹571k return. But in the combined sweep with the spread, Aggressive DD blew out to 2.90% and Sharpe dropped to 3.22. BalancedLow (Prem=15, PB=3.0×) kept DD at 1.24% with Sharpe 4.47 — the best combined risk-adjusted return of all 21 runs. The spread already provides theta income; the ladder just needs a modest directional kicker, not a home-run swing.

---

## Component 3: GammaBurst

### What It Does
Buys near-expiry option (CurrentExpiryDT, DTE < 2d) on same signal. Captures gamma spikes on expiry day / day before. Zero margin.

### DT0/DT1 Split
Different gamma/theta dynamics require separate parameters:
- DT0 (expiry day, DTE < 0.5d): fast gamma spikes, tight SL, quick PB
- DT1 (day before, DTE 0.5–2d): moves develop over hours, loose SL, higher PB

### V2 Parameters (Final)

| Parameter | DT0 | DT1 |
|-----------|-----|-----|
| TargetPremium | 22 | 25 |
| ProfitBookMultiple | 2.5× | 3.0× |
| SLPct | 20% | 40% |

Shared:
- GammaBurstBearMinVIX = 13 (bear gamma at low VIX is toxic)
- GammaBurstDT0Threshold = 0.5d
- GammaBurstMaxDTE = 2.0
- GammaBurstMinDTE = 0.0

### GammaBurst Exit Logic (MonitorGammaBurstTrades)
```
PB:  currentPremium > entryPremium × PBMult
SL:  currentPremium < entryPremium × (1 - SLPct/100)
```

---

## Files Changed (Summary)

| File | What Changed |
|------|-------------|
| **StrategyParameters.cs** | Spread params: SLF=0.75, Swing=1.25, POP=90, BPR=0.30. Added EnableTargetMaxLossAdj, MinSellPremium, MinBuyPremium, MaxSpreadWidth, MaxConcurrentSpreads, ForceCloseOldestOnOverflow, OptunaMode. Legacy far-leg params commented out. |
| **GammaBurstParameters.cs** | New file. DT0/DT1 split params, EnableLadder, EnableGammaBurst flags. |
| **LadderTradeParameters.cs** | New file. Independent far leg params: Prem=15, PB=3.0, SL=20, Spot=1.5. |
| **TradeMonitorAndExits.cs** | Added MonitorLadderTrades(), MonitorGammaBurstTrades(). Spread exit simplified (no IsPartiallyBooked). Old TryBookLadderLeg/SquareOffBuyLegs commented out. |
| **TradeSetup.cs** | Split CreateLadderLegs → CreateSpreadLegs + CreateLadderTradeLeg + CreateGammaBurstLeg. Added spread guards (MinSell, MinBuy, MaxWidth adjust strikes, not reject). |
| **TradeEntry.cs** | ExecuteFreshTrade fires spread + LadderTrade + GammaBurst independently. |
| **TradeRecord.cs** | Added BullLadderTrade, BearLadderTrade, BullGammaBurst, BearGammaBurst to TradeType enum. SpreadPnL = CurrentPnL. IsPartiallyBooked = false. |
| **Main.cs** | Unified CSV export (one row per trade, TradeType column). OPTUNA_METRICS console output. |
| **SignalRuleBased.cs** | Unchanged. |
| **Indicator.cs** | Unchanged. |

---

## Optimisation History

| Phase | What | Trials | Result |
|-------|------|--------|--------|
| 1. Architecture | Decouple 3-leg → Spread + LadderTrade + GammaBurst | — | Baseline ₹173k, PF 1.22, DD 27% |
| 2. GammaBurst DT0 | Premium, PB, SL for expiry day | 40 | Prem=22, PB=2.5×, SL=20% |
| 3. GammaBurst DT1 | Premium, PB, SL for day before | 40 | Prem=25, PB=3.0×, SL=40% |
| 4. GammaBurst filter | VIX floor for bear | — | BearMinVIX=13 |
| 5. LadderTrade standalone | Premium, PB, SL, SpotThr | 60 | Standalone winner: Prem=50, PB=4.0, SL=20, Spot=2.0 |
| 6. Spread Optuna | SLF, Swing, POP, BPR (all components active) | 16 | 3 spread candidates: Spread-A (SLF=0.75, Sw=1.25), Spread-B (SLF=0.75, Sw=0.75), Spread-C (SLF=1.5, Sw=0.75) |
| 7. Combined grid | 3 spreads × 7 LT configs, GammaBurst locked | 21 | Winner: Spread-A + LT2-BalancedLow (Prem=15, PB=3.0, SL=20, Spot=1.5) |
| 8. Spread guards | MinSell, MinBuy, MaxWidth, MaxConcurrent, SkipOverflow | manual | MinSell≥20, MinBuy≥5, MaxWidth≤1000, MaxConcurrent=3 |

### Combined Sweep — Top 5 (by Sharpe)

| Rank | Spread | Ladder | Profit | Sharpe | DD% | Calmar | PF |
|------|--------|--------|--------|--------|-----|--------|-----|
| 1 | Spread-A | LT2-BalancedLow | ₹302,694 | 4.47 | 1.24 | 5.86 | 2.03 |
| 2 | Spread-A | LT1-Conservative | ₹267,499 | 4.46 | 1.20 | 5.64 | 2.09 |
| 3 | Spread-A | LT6-HiddenGemA | ₹256,393 | 4.36 | 1.21 | 5.40 | 2.04 |
| 4 | Spread-A | LT3-BalancedMid | ₹279,953 | 4.03 | 1.56 | 4.45 | 1.90 |
| 5 | Spread-C | LT2-BalancedLow | ₹269,709 | 3.90 | 1.43 | 4.77 | 1.91 |

---

## Final Performance (V1 vs V2)

| Metric | V1 (Original) | V2 (Final) |
|--------|---------------|------------|
| Total Return | 127.5% | 134.3% |
| CAGR | 25.5% | 26.9% |
| Sharpe (√365) | 2.08 | 2.84 |
| Sortino (√365) | 4.67 | 6.01 |
| Max DD | 8.57% | 10.80% |
| Profit Factor | 1.66 | 1.85 |
| Win Rate | 44.0% | 39.6% |
| Calmar | 2.97 | 2.49 |
| CV Annual Returns | 69.4% | 51.1% |
| Positive Quarters | 85.7% | 95.2% |
| Gain-to-Pain | 0.66 | 0.85 |
| Ulcer Index | 2.23 | 2.35 |
| UPI | 57.22 | 57.10 |

> Note: Sharpe/Sortino above are from the daily equity curve annualised with √365. The combined sweep Sharpe (4.47) uses a different calculation basis (trade-level, not daily MTM). Both are correct, different measurement.

---

## V2 Final Parameter Snapshot (Verified)

```
Spread:  SLF=0.75 · Swing=1.25 · POP=90 · BPR=0.30 · TMLP=0.25
         MaxConcurrent=3 · SkipOverflow · MinSell≥20 · MinBuy≥5 · MaxWidth≤1000
         CreditUB=2000 · EnableTargetMaxLossAdj=true
Ladder:  Premium=15 · PBMult=3.0 · PBAdd=25 · SLPct=20 · SpotThr=1.50
Gamma:   DT0: Prem=22 PB=2.5× SL=20% | DT1: Prem=25 PB=3.0× SL=40%
         BearMinVIX=13 · DT0Threshold=0.5 · MaxDTE=2.0
Shared:  Capital=₹185k · MDE=4 · Bar=30min · TargetSellPrem=15
```

---

## Next Steps

1. **Production conversion** — port to QX.Blitz (see conversion-guide.md)
2. **Walk-forward validation** — test on unseen data beyond Feb 2026
3. **Margin monitoring** — P95 margin ₹212k, peak ₹247k per CSV
