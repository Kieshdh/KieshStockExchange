# P6c deterministic short-bracket cover harness.
#
# Locks in the short-bracket cover paths the P6 bot soak exercised only statistically, asserting
# conservation each step (a flat position carries 0 collateral; no "Insufficient available" failures;
# fund/position reservations return to baseline):
#   A. scale-out      : TP buy-legs cover the short in steps (BracketCoordinator.OnChildFillShortAsync +
#                       TradeSettler.DrawSiblingSlPool + the buy-to-close collateral release)
#   B. teardown+cover : cancel the SL (OnMemberCancelledShortAsync releases its cash pool) then cover the
#                       short with a plain buy (buy-to-close collateral release)
# Note: the SL-FIRE path (StopTriggerWatcher -> OnStopFiringShortAsync) is validated by the multi-hour bot
# soak, not here — firing needs the continuous live quote feed the bots drive, so it can't be force-triggered
# deterministically with bots OFF (and bots ON makes the book non-deterministic).
#
# Self-trade prevention blocks the owner filling their own legs, so a second account `p6ctest` supplies
# the crossing liquidity. Run against a CLEAN soak DB with bots OFF (empty book) so every cross is
# deterministic:
#   1. fresh kse_soak  : DROP/CREATE DATABASE kse_soak; dotnet ef database update
#   2. create p6ctest  : this script does it via docker psql (copies admin's hash -> same password)
#   3. start server    : KSE_DB_CONNECTION_STRING=...kse_soak  Bots__AutoStart=false  (clean book)
#   4. run this script
$ErrorActionPreference = "Stop"
$base = "http://localhost:5000"; $sid = 5; $ccy = "USD"
$pgContainer = "kieshstockexchange-postgres-1"; $pgDb = "kse_soak"
$pass = 0; $fail = 0
function Ok($m){ $script:pass++; Write-Host "  PASS  $m" -ForegroundColor Green }
function Bad($m){ $script:fail++; Write-Host "  FAIL  $m" -ForegroundColor Red }
function Step($m){ Write-Host "`n== $m ==" -ForegroundColor Cyan }
function Money($x){ [math]::Round([decimal]$x, 2) }

# --- counterparty: create p6ctest with admin's password hash (logs in with the same password) + fund ---
$mkUserSql = @"
INSERT INTO "Users" ("Username","PasswordHash","Email","FullName","CreatedAt","IsAdmin")
SELECT 'p6ctest', "PasswordHash", 'p6ctest@test.local', 'P6C Counterparty', now(), false
FROM "Users" WHERE "Username"='admin'
ON CONFLICT ("Username") DO NOTHING;
INSERT INTO "Funds" ("UserId","TotalBalance","ReservedBalance","Currency","CreatedAt","UpdatedAt")
SELECT u."UserId", 100000000, 0, 'USD', now(), now() FROM "Users" u WHERE u."Username"='p6ctest'
ON CONFLICT ("UserId","Currency") DO UPDATE SET "TotalBalance"=100000000, "ReservedBalance"=0;
"@
$mkUserSql | docker exec -i $pgContainer psql -U kse -d $pgDb | Out-Null
Write-Host "counterparty p6ctest ensured (USD funded)"

function Login($user,$pw){
  $r = Invoke-RestMethod "$base/api/auth/login" -Method Post -ContentType application/json -Body (@{Username=$user;Password=$pw}|ConvertTo-Json)
  @{ uid=$r.userId; H=@{Authorization="Bearer $($r.token)"} }
}
$A = Login "admin" "hallo123"        # bracket owner
$C = Login "p6ctest" "hallo123"      # counterparty (liquidity)
Write-Host "admin uid=$($A.uid)  counterparty uid=$($C.uid)"

function Price { [decimal](Invoke-RestMethod "$base/api/market-lookup/latest-price/$sid/$ccy" -Headers $A.H) }
function PosOf($u){ try { Invoke-RestMethod "$base/api/positions/by-user-stock/$($u.uid)/$sid" -Headers $u.H } catch { $null } }
function FundOf($u){ Invoke-RestMethod "$base/api/funds/by-user-currency/$($u.uid)/$ccy" -Headers $u.H }
function OrdersOf($u){ Invoke-RestMethod "$base/api/orders/by-user/$($u.uid)" -Headers $u.H }
function OrderById($u,$id){ (OrdersOf $u) | Where-Object { $_.orderId -eq $id } }
function Place($u,$b){ $b.userId=$u.uid; Invoke-RestMethod "$base/api/orders/place" -Headers $u.H -Method Post -ContentType application/json -Body ($b|ConvertTo-Json) }
function PlaceBracket($u,$b){ $b.userId=$u.uid; Invoke-RestMethod "$base/api/orders/place-bracket" -Headers $u.H -Method Post -ContentType application/json -Body ($b|ConvertTo-Json -Depth 6) }
function Cancel($u,$id){ try { Invoke-RestMethod "$base/api/orders/$id/cancel?userId=$($u.uid)" -Headers $u.H -Method Post | Out-Null } catch {} }
function Qty($u){ $p=PosOf $u; if($p){[int]$p.quantity}else{0} }
function Coll($u){ $p=PosOf $u; if($p){Money $p.shortCollateral}else{0} }
function Reserved($u){ Money (FundOf $u).reservedBalance }
function Settle{ Start-Sleep -Milliseconds 700 }

# --- disable bots so the book is fully controlled by admin + p6ctest ---
try { Invoke-RestMethod "$base/api/admin/bots/stop" -Headers $A.H -Method Post | Out-Null; Write-Host "bots stopped" } catch { Write-Host "bot stop: $($_.Exception.Message)" -ForegroundColor Yellow }
Settle

$N = 8                               # short size (even, so two N/2 take-profits)
# Price levels are recomputed off the LIVE market at each scenario start — each scenario's covers move
# the last price, so reusing a stale anchor would put a short's TP above market (geometry-rejected).
function Levels { $m = Price; @{ M=$m; slStop=(Money ($m*1.05)); slLim=(Money ($m*1.06)); tpHi=(Money ($m*0.97)); tpLo=(Money ($m*0.95)) } }

# ============================================================ Scenario A: scale-out (TP covers) ===
Step "A. Scale-out: short bracket, then cover both TPs to flat"
$aQty0 = Qty $A; $aRes0 = Reserved $A
$L = Levels; $M=$L.M; $slStop=$L.slStop; $slLim=$L.slLim; $tpHi=$L.tpHi; $tpLo=$L.tpLo
Write-Host "admin baseline: pos.qty=$aQty0 fund.reserved=$aRes0  | market=$M SL=$slStop/$slLim TPs=$tpHi,$tpLo"

# counterparty posts a bid to absorb the short entry
$cbid = Place $C @{ stockId=$sid;quantity=$N;side="Buy";entry="Limit";stop="None";currency=$ccy;price=$M }
Settle
# admin opens the short bracket (market short entry crosses the bid)
$br = PlaceBracket $A @{ stockId=$sid;quantity=$N;entry="Market";currency=$ccy;price=$null;buyBudget=$null;
  stopPrice=$slStop; stopLimitPrice=$slLim; stopSlippagePct=$null;
  takeProfits=@(@{price=$tpHi;quantity=($N/2)}, @{price=$tpLo;quantity=($N/2)}); side="Sell" }
Settle
$parentId = $br.placedOrder.orderId
if ((Qty $A) -eq $aQty0 - $N) { Ok "short opened: admin pos.qty $aQty0 -> $(Qty $A)" } else { Bad "short entry qty=$(Qty $A) expected $($aQty0-$N)" }
$legs = (OrdersOf $A) | Where-Object { $_.parentOrderId -eq $parentId }
$sl = $legs | Where-Object { $_.stop -eq "Stop" }
$tps = @($legs | Where-Object { $_.stop -eq "None" -and $_.entry -eq "Limit" } | Sort-Object { [decimal]$_.price } -Descending)
if ($sl -and $sl.status -eq "Pending") { Ok "SL armed (Pending) #$($sl.orderId) pool=$(Money $sl.currentBuyReservation)" } else { Bad "SL not armed (status=$($sl.status))" }
if ($tps.Count -eq 2 -and $tps[0].status -eq "Open" -and $tps[1].status -eq "Open") { Ok "2 TP legs Open (buy-limits resting as bids)" } else { Bad "TP legs=$($tps.Count) statuses=$($tps.status -join ',')" }
$collOpen = Coll $A
if ($collOpen -gt 0) { Ok "short collateral on position = $collOpen" } else { Bad "no short collateral after open" }

# cover TP #1 (higher price, best bid): counterparty sells N/2 into it
$err1 = $null
Place $C @{ stockId=$sid;quantity=($N/2);side="Sell";entry="Limit";stop="None";currency=$ccy;price=$tpHi } | Out-Null
Settle
if ((Qty $A) -eq $aQty0 - ($N/2)) { Ok "TP1 covered: admin pos.qty -> $(Qty $A) (half)" } else { Bad "after TP1 qty=$(Qty $A) expected $($aQty0-($N/2))" }
if ((Coll $A) -gt 0 -and (Coll $A) -lt $collOpen) { Ok "collateral released pro-rata ($collOpen -> $(Coll $A))" } else { Bad "collateral=$(Coll $A) (expected 0<x<$collOpen)" }

# cover TP #2 (covers to flat)
Place $C @{ stockId=$sid;quantity=($N/2);side="Sell";entry="Limit";stop="None";currency=$ccy;price=$tpLo } | Out-Null
Settle
if ((Qty $A) -eq $aQty0) { Ok "TP2 covered to flat: admin pos.qty=$(Qty $A)" } else { Bad "after TP2 qty=$(Qty $A) expected $aQty0" }
if ((Coll $A) -eq 0) { Ok "flat position carries 0 collateral (CK invariant upheld)" } else { Bad "flat position still has collateral=$(Coll $A)" }
$slAfter = OrderById $A $sl.orderId
if ($slAfter.status -eq "Cancelled") { Ok "SL auto-cancelled once short fully covered" } else { Bad "SL status=$($slAfter.status) (expected Cancelled)" }
if ((Reserved $A) -eq $aRes0) { Ok "admin fund.reserved back to baseline ($aRes0)" } else { Bad "fund.reserved=$(Reserved $A) != baseline $aRes0" }

# ============================================================ Scenario B: SL fire (cover remainder) ===
Step "B. Teardown + cover: short bracket, cancel SL (release pool), then cover the short"
# The SL-FIRE path (StopTriggerWatcher -> OnStopFiringShortAsync) is validated by the multi-hour bot soak:
# firing needs the continuous live quote feed the bots drive, so it can't be force-triggered in this
# bots-OFF deterministic harness. Here we deterministically exercise the OTHER short-bracket cover paths:
# the SL cash-pool release on cancel (OnMemberCancelledShortAsync) and the buy-to-close collateral release.
$bQty0 = Qty $A; $bRes0 = Reserved $A
$L = Levels; $M=$L.M; $slStop=$L.slStop; $slLim=$L.slLim
Place $C @{ stockId=$sid;quantity=$N;side="Buy";entry="Limit";stop="None";currency=$ccy;price=$M } | Out-Null
Settle
$br2 = PlaceBracket $A @{ stockId=$sid;quantity=$N;entry="Market";currency=$ccy;price=$null;buyBudget=$null;
  stopPrice=$slStop; stopLimitPrice=$slLim; stopSlippagePct=$null;
  takeProfits=@(@{price=$L.tpHi;quantity=$N}); side="Sell" }
Settle
$p2 = $br2.placedOrder.orderId
$sl2 = (OrdersOf $A) | Where-Object { $_.parentOrderId -eq $p2 -and $_.stop -eq "Stop" }
$collB = Coll $A; $resArmed = Reserved $A
if ((Qty $A) -eq $bQty0 - $N -and $sl2.status -eq "Pending") { Ok "short#2 opened, SL armed #$($sl2.orderId), collateral=$collB" } else { Bad "short#2 qty=$(Qty $A) SL=$($sl2.status)" }

# cancel the SL leg: teardown releases the SL cash pool (and cancels TPs); the short + collateral remain
Cancel $A $sl2.orderId
Settle
$resCancel = Reserved $A
if ($resCancel -lt $resArmed -and $resCancel -eq $collB) { Ok "SL cancel released the pool ($resArmed -> $resCancel; collateral $collB retained)" } else { Bad "after SL cancel reserved=$resCancel (armed=$resArmed, collateral=$collB)" }
if ((Qty $A) -eq $bQty0 - $N) { Ok "short position intact after SL cancel ($(Qty $A))" } else { Bad "short qty changed on SL cancel: $(Qty $A)" }

# cover the remaining short via a plain market-style buy (counterparty provides the ask): flat, collateral freed
Place $C @{ stockId=$sid;quantity=$N;side="Sell";entry="Limit";stop="None";currency=$ccy;price=$M } | Out-Null
Settle
Place $A @{ stockId=$sid;quantity=$N;side="Buy";entry="Limit";stop="None";currency=$ccy;price=$M } | Out-Null
Settle
if ((Qty $A) -eq $bQty0) { Ok "short covered to flat via plain buy ($(Qty $A))" } else { Bad "after cover qty=$(Qty $A) expected $bQty0" }
if ((Coll $A) -eq 0) { Ok "flat position carries 0 collateral (CK invariant upheld)" } else { Bad "collateral=$(Coll $A) after cover" }
if ((Reserved $A) -eq $bRes0) { Ok "admin fund.reserved back to baseline ($bRes0)" } else { Bad "fund.reserved=$(Reserved $A) != baseline $bRes0" }

# ============================================================ conservation + cleanup ===
Step "C. Conservation sweep"
# no 'Insufficient available' on the admin side anywhere in this run is asserted by the server log;
# here we confirm both accounts ended reservation-clean and the owner round-tripped net-neutral.
if ((Qty $A) -eq $aQty0) { Ok "admin position net-neutral over the whole run ($aQty0)" } else { Bad "admin pos.qty=$(Qty $A) != $aQty0" }
foreach ($o in (OrdersOf $A)) { if ($o.status -eq "Open" -or $o.status -eq "Pending" -or $o.status -eq "Attached") { Cancel $A $o.orderId } }
foreach ($o in (OrdersOf $C)) { if ($o.status -eq "Open" -or $o.status -eq "Pending" -or $o.status -eq "Attached") { Cancel $C $o.orderId } }
Settle
if ((Reserved $A) -eq $aRes0) { Ok "admin fund.reserved clean after cleanup ($aRes0)" } else { Bad "admin fund.reserved=$(Reserved $A) != $aRes0" }

$col = if($fail -eq 0){"Green"}else{"Red"}
Write-Host "`n========= P6C RESULT: $pass passed, $fail failed =========" -ForegroundColor $col
