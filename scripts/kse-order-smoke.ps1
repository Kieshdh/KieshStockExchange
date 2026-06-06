# KSE order-engine smoke test - drives the server HTTP API as the 'admin' user.
# Tests each order type + verifies DB persistence and the reservation invariant.
$ErrorActionPreference = "Stop"
$base = "http://localhost:5000"
$sid  = 5            # GOOG
$ccy  = "USD"
$pass = 0; $fail = 0
function Ok($m){ $script:pass++; Write-Host "  PASS  $m" -ForegroundColor Green }
function Bad($m){ $script:fail++; Write-Host "  FAIL  $m" -ForegroundColor Red }
function Step($m){ Write-Host "`n== $m ==" -ForegroundColor Cyan }

$login = Invoke-RestMethod "$base/api/auth/login" -Method Post -ContentType application/json -Body '{"Username":"admin","Password":"hallo123"}'
$uid = $login.userId
$H = @{ Authorization = "Bearer $($login.token)" }
Write-Host "Logged in as userId=$uid"

function Price { (Invoke-RestMethod "$base/api/market-lookup/latest-price/$sid/$ccy" -Headers $H) }
function Fund  { Invoke-RestMethod "$base/api/funds/by-user-currency/$uid/$ccy" -Headers $H }
function Pos   { try { Invoke-RestMethod "$base/api/positions/by-user-stock/$uid/$sid" -Headers $H } catch { $null } }
function Orders { Invoke-RestMethod "$base/api/orders/by-user/$uid" -Headers $H }
function Place($body){ Invoke-RestMethod "$base/api/orders/place" -Headers $H -Method Post -ContentType application/json -Body ($body|ConvertTo-Json) }
function PlaceBracket($body){ Invoke-RestMethod "$base/api/orders/place-bracket" -Headers $H -Method Post -ContentType application/json -Body ($body|ConvertTo-Json -Depth 5) }
function Cancel($id){ Invoke-RestMethod "$base/api/orders/$id/cancel?userId=$uid" -Headers $H -Method Post }
function ModifyLeg($id,$price,$qty){ Invoke-RestMethod "$base/api/orders/$id/modify-leg" -Headers $H -Method Post -ContentType application/json -Body (@{userId=$uid;price=$price;quantity=$qty}|ConvertTo-Json) }
function ModifyStop($id,$q,$sp,$lp){ Invoke-RestMethod "$base/api/orders/$id/modify-stop" -Headers $H -Method Post -ContentType application/json -Body (@{userId=$uid;quantity=$q;stopPrice=$sp;limitPrice=$lp}|ConvertTo-Json) }
function OrderById($id){ (Orders) | Where-Object { $_.orderId -eq $id } }
function Money($x){ [math]::Round([decimal]$x,2) }

$mkt = Price
Write-Host "GOOG market price = $mkt"
$lowBuy = Money ($mkt * 0.5)
$f0 = Fund; $p0 = Pos
$res0 = [decimal]$f0.reservedBalance
$posRes0 = if($p0){[int]$p0.reservedQuantity}else{0}
$posQty0 = if($p0){[int]$p0.quantity}else{0}
Write-Host "baseline: fund.reserved=$res0  pos.qty=$posQty0 pos.reserved=$posRes0"

Step "1. Limit buy (resting) + cancel"
$r = Place @{ userId=$uid;stockId=$sid;quantity=2;side="Buy";entry="Limit";stop="None";currency=$ccy;price=$lowBuy }
Start-Sleep -Milliseconds 400
$o = OrderById $r.placedOrder.orderId
if ($o -and $o.status -eq "Open" -and $o.entry -eq "Limit") { Ok "limit buy rests Open (#$($o.orderId))" } else { Bad "limit buy status=$($o.status)" }
if ((Money (Fund).reservedBalance) -gt (Money $res0)) { Ok "fund reserved rose while resting" } else { Bad "fund reserved did not rise" }
Cancel $r.placedOrder.orderId | Out-Null; Start-Sleep -Milliseconds 400
$o = OrderById $r.placedOrder.orderId
if ($o.status -eq "Cancelled") { Ok "limit buy cancelled" } else { Bad "after cancel status=$($o.status)" }
if ((Money (Fund).reservedBalance) -eq (Money $res0)) { Ok "fund reserved back to baseline" } else { Bad "fund reserved != baseline" }

Step "2. Market buy (fills)"
$txBefore = (Invoke-RestMethod "$base/api/transactions/by-user/$uid" -Headers $H).Count
$r = Place @{ userId=$uid;stockId=$sid;quantity=3;side="Buy";entry="Market";stop="None";currency=$ccy;buyBudget=(Money ($mkt*10)) }
Start-Sleep -Milliseconds 600
if ($r.status -eq "Filled" -or $r.status -eq "PartialFill") { Ok "market buy $($r.status)" } else { Bad "market buy status=$($r.status)" }
$pq = [int](Pos).quantity
if ($pq -ge $posQty0 + 1) { Ok "position qty rose $posQty0 -> $pq" } else { Bad "position qty=$pq (was $posQty0)" }
$txAfter = (Invoke-RestMethod "$base/api/transactions/by-user/$uid" -Headers $H).Count
if ($txAfter -gt $txBefore) { Ok "transaction row(s) persisted ($txBefore -> $txAfter)" } else { Bad "no new transaction rows" }

Step "3. Stop-market sell (armed trigger) + modify + cancel"
$posResA = [int](Pos).reservedQuantity
$r = Place @{ userId=$uid;stockId=$sid;quantity=2;side="Sell";entry="Market";stop="Stop";currency=$ccy;stopPrice=(Money ($mkt*0.7)) }
Start-Sleep -Milliseconds 400
$o = OrderById $r.placedOrder.orderId
if ($o.status -eq "Pending" -and $o.stop -eq "Stop") { Ok "stop armed (Pending, Stop) #$($o.orderId)" } else { Bad "stop status=$($o.status) stop=$($o.stop)" }
$posResB = [int](Pos).reservedQuantity
if ($posResB -gt $posResA) { Ok "sell-stop reserved shares ($posResA -> $posResB)" } else { Bad "reserved qty unchanged ($posResA -> $posResB)" }
ModifyStop $r.placedOrder.orderId $null (Money ($mkt*0.65)) $null | Out-Null
if ((OrderById $r.placedOrder.orderId).status -eq "Pending") { Ok "modify-stop kept it armed" } else { Bad "after modify not armed" }
Cancel $r.placedOrder.orderId | Out-Null; Start-Sleep -Milliseconds 400
if ((OrderById $r.placedOrder.orderId).status -eq "Cancelled") { Ok "stop cancelled" } else { Bad "stop not cancelled" }
if ([int](Pos).reservedQuantity -eq $posResA) { Ok "share reservation released to baseline" } else { Bad "pos.reserved not restored" }

Step "4. Stop-limit sell (armed) + cancel"
$r = Place @{ userId=$uid;stockId=$sid;quantity=2;side="Sell";entry="Limit";stop="Stop";currency=$ccy;stopPrice=(Money ($mkt*0.7));price=(Money ($mkt*0.69)) }
Start-Sleep -Milliseconds 400
$o = OrderById $r.placedOrder.orderId
if ($o.status -eq "Pending" -and $o.stop -eq "Stop" -and $o.entry -eq "Limit") { Ok "stop-limit armed #$($o.orderId)" } else { Bad "stop-limit status=$($o.status) entry=$($o.entry)" }
Cancel $r.placedOrder.orderId | Out-Null; Start-Sleep -Milliseconds 400
if ((OrderById $r.placedOrder.orderId).status -eq "Cancelled") { Ok "stop-limit cancelled" } else { Bad "not cancelled" }

Step "5. Bracket (unfilled) - dormant legs, per-leg cancel (F5), cancel-SL-keeps-TPs"
$r = PlaceBracket @{ userId=$uid;stockId=$sid;quantity=6;entry="Limit";currency=$ccy;price=$lowBuy;buyBudget=$null;
  stopPrice=(Money ($lowBuy*0.9)); stopLimitPrice=$null; stopSlippagePct=$null;
  takeProfits=@(@{price=(Money ($mkt*1.5));quantity=3}, @{price=(Money ($mkt*1.8));quantity=3}) }
Start-Sleep -Milliseconds 600
$all = Orders
$parent = $all | Where-Object { $_.orderId -eq $r.placedOrder.orderId }
$legs = $all | Where-Object { $_.parentOrderId -eq $r.placedOrder.orderId }
if ($parent.status -eq "Open") { Ok "bracket parent rests Open #$($parent.orderId)" } else { Bad "parent status=$($parent.status)" }
$attached = $legs | Where-Object { $_.status -eq "Attached" }
if (@($attached).Count -eq 3) { Ok "3 dormant Attached legs persisted" } else { Bad "attached legs=$(@($attached).Count) (expected 3)" }
$slLeg = $legs | Where-Object { $_.stop -eq "Stop" }
$tpLegs = @($legs | Where-Object { $_.stop -eq "None" -and $_.entry -eq "Limit" } | Sort-Object price)
if ($slLeg) { Ok "SL leg present (Attached, Stop)" } else { Bad "no SL leg" }
if ($tpLegs.Count -eq 2) { Ok "2 TP legs present" } else { Bad "tp legs=$($tpLegs.Count)" }
# --- F5 dormant-leg MODIFY (ModifyBracketLegAsync) ---
$newTpPrice = Money ($mkt*1.6); $newTpQty = 2
ModifyLeg $tpLegs[0].orderId $newTpPrice $newTpQty | Out-Null; Start-Sleep -Milliseconds 400
$t0 = (Orders) | Where-Object {$_.orderId -eq $tpLegs[0].orderId}
if ([decimal]$t0.price -eq $newTpPrice -and [int]$t0.quantity -eq $newTpQty) { Ok "dormant TP modified in place (price+qty)" } else { Bad "TP modify price=$($t0.price) qty=$($t0.quantity)" }
$t1 = (Orders) | Where-Object {$_.orderId -eq $tpLegs[1].orderId}
if ([decimal]$t1.price -eq [decimal]$tpLegs[1].price) { Ok "sibling TP untouched by edit (F12)" } else { Bad "sibling TP changed" }
$newSl = Money ($lowBuy*0.8)
ModifyLeg $slLeg.orderId $newSl ($slLeg.quantity) | Out-Null; Start-Sleep -Milliseconds 400
$s0 = (Orders) | Where-Object {$_.orderId -eq $slLeg.orderId}
if ([decimal]$s0.stopPrice -eq $newSl) { Ok "dormant SL trigger modified in place" } else { Bad "SL modify stopPrice=$($s0.stopPrice)" }
$bad = $null; try { $bad = ModifyLeg $tpLegs[0].orderId (Money ($lowBuy*0.5)) 2 } catch { }
$tChk = (Orders) | Where-Object {$_.orderId -eq $tpLegs[0].orderId}
if ($bad.status -eq "InvalidParameters" -and [decimal]$tChk.price -eq $newTpPrice) { Ok "invalid TP edit (below entry) rejected, no mutation" } else { Bad "invalid edit not rejected (status=$($bad.status) price=$($tChk.price))" }
# --- per-leg cancel + cancel-SL-keeps-TPs ---
Cancel ($tpLegs[1].orderId) | Out-Null; Start-Sleep -Milliseconds 500
$after = Orders
if (($after | Where-Object {$_.orderId -eq $tpLegs[1].orderId}).status -eq "Cancelled") { Ok "dormant TP per-leg cancelled (F5)" } else { Bad "TP not cancelled" }
if (($after | Where-Object {$_.orderId -eq $tpLegs[0].orderId}).status -eq "Attached") { Ok "sibling TP still Attached" } else { Bad "sibling TP disturbed" }
if (($after | Where-Object {$_.orderId -eq $parent.orderId}).status -eq "Open") { Ok "parent still Open after TP cancel" } else { Bad "parent changed" }
Cancel ($slLeg.orderId) | Out-Null; Start-Sleep -Milliseconds 500
$after = Orders
if (($after | Where-Object {$_.orderId -eq $slLeg.orderId}).status -eq "Cancelled") { Ok "SL cancelled" } else { Bad "SL not cancelled" }
if (($after | Where-Object {$_.orderId -eq $tpLegs[0].orderId}).status -eq "Attached") { Ok "remaining TP survives (bracket now TP-only)" } else { Bad "remaining TP gone - teardown bug" }
if (($after | Where-Object {$_.orderId -eq $parent.orderId}).status -eq "Open") { Ok "parent survives SL-cancel (no teardown)" } else { Bad "parent torn down on SL cancel" }
Cancel ($parent.orderId) | Out-Null; Start-Sleep -Milliseconds 500
if (((Orders) | Where-Object {$_.orderId -eq $parent.orderId}).status -eq "Cancelled") { Ok "parent cancelled (cleanup)" } else { Bad "parent not cancelled" }

Step "5b. Cleanup: market-sell the 3 shares acquired in step 2 (keep account net-neutral)"
$r = Place @{ userId=$uid;stockId=$sid;quantity=3;side="Sell";entry="Market";stop="None";currency=$ccy }
Start-Sleep -Milliseconds 600
if ($r.status -eq "Filled") { Ok "sold 3 back ($($r.status))" } else { Bad "sell-back status=$($r.status)" }
$pqEnd = [int](Pos).quantity
if ($pqEnd -eq $posQty0) { Ok "position qty net-neutral ($posQty0)" } else { Bad "position qty=$pqEnd (baseline $posQty0)" }

Step "7. Resting limit short (F14): place-time collateral hold + cancel"
$p7 = Pos
$avail7 = if($p7){[int]$p7.quantity - [int]$p7.reservedQuantity}else{0}
$res7 = [decimal](Fund).reservedBalance
$shortQty = 2
$sellQty = $avail7 + $shortQty          # forces exactly $shortQty uncovered short shares
$shortPrice = Money ($mkt * 1.5)        # above market -> rests as a maker, doesn't cross
$expColl = Money ($shortPrice * $shortQty)
$r = Place @{ userId=$uid;stockId=$sid;quantity=$sellQty;side="Sell";entry="Limit";stop="None";currency=$ccy;price=$shortPrice }
Start-Sleep -Milliseconds 500
$o = OrderById $r.placedOrder.orderId
if ($o -and $o.status -eq "Open") { Ok "resting short rests Open (#$($o.orderId): sell $sellQty @ $shortPrice, $shortQty uncovered)" } else { Bad "resting short status=$($o.status)" }
$delta7 = Money ([decimal](Fund).reservedBalance - $res7)
if ($delta7 -eq $expColl) { Ok "fund reserved rose by short collateral (delta=$delta7 == $expColl)" } else { Bad "collateral hold wrong: delta=$delta7 expected $expColl" }
if ($avail7 -gt 0) {
  $posRes7 = [int](Pos).reservedQuantity
  if ($posRes7 -ge $avail7) { Ok "covered part reserved $avail7 share(s) on position (reserved=$posRes7)" } else { Bad "covered share reserve missing (reserved=$posRes7, expected>=$avail7)" }
}
Cancel $r.placedOrder.orderId | Out-Null; Start-Sleep -Milliseconds 500
if ((OrderById $r.placedOrder.orderId).status -eq "Cancelled") { Ok "resting short cancelled" } else { Bad "resting short not cancelled" }
if ((Money (Fund).reservedBalance) -eq (Money $res7)) { Ok "short collateral released to baseline ($res7)" } else { Bad "fund.reserved=$((Money (Fund).reservedBalance)) != baseline $res7" }

Step "6. Conservation sweep"
$fEnd = Fund
if ((Money $fEnd.reservedBalance) -eq (Money $res0)) { Ok "fund.reserved back to baseline ($res0)" } else { Bad "fund.reserved=$($fEnd.reservedBalance) != baseline $res0" }
$pEnd = Pos
$posResEnd = if($pEnd){[int]$pEnd.reservedQuantity}else{0}
if ($posResEnd -eq $posRes0) { Ok "pos.reserved back to baseline ($posRes0)" } else { Bad "pos.reserved=$posResEnd != baseline $posRes0" }
# Note: per-order CurrentSellReservedQty is a runtime-only engine field (not persisted), so the
# DB-read API returns 0 for it. The observable conservation checks are fund.reserved + pos.reserved
# returning to baseline (above), which both passed.

$col = if($fail -eq 0){"Green"}else{"Red"}
Write-Host "`n================ RESULT: $pass passed, $fail failed ================" -ForegroundColor $col