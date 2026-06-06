# P5 trailing-stop smoke: arm / ratchet+flush+persist / cancel. Drives the live engine as admin.
$ErrorActionPreference = "Stop"
$base="http://localhost:5000"; $sid=5; $ccy="USD"
$pass=0; $fail=0
function Ok($m){ $script:pass++; Write-Host "  PASS  $m" -ForegroundColor Green }
function Bad($m){ $script:fail++; Write-Host "  FAIL  $m" -ForegroundColor Red }
function Step($m){ Write-Host "`n== $m ==" -ForegroundColor Cyan }
$login=Invoke-RestMethod "$base/api/auth/login" -Method Post -ContentType application/json -Body '{"Username":"admin","Password":"hallo123"}'
$uid=$login.userId; $H=@{Authorization="Bearer $($login.token)"}
function Price { [decimal](Invoke-RestMethod "$base/api/market-lookup/latest-price/$sid/$ccy" -Headers $H) }
function Pos { try { Invoke-RestMethod "$base/api/positions/by-user-stock/$uid/$sid" -Headers $H } catch { $null } }
function Orders { Invoke-RestMethod "$base/api/orders/by-user/$uid" -Headers $H }
function OrderById($id){ (Orders) | Where-Object { $_.orderId -eq $id } }
function Place($b){ Invoke-RestMethod "$base/api/orders/place" -Headers $H -Method Post -ContentType application/json -Body ($b|ConvertTo-Json) }
function Cancel($id){ Invoke-RestMethod "$base/api/orders/$id/cancel?userId=$uid" -Headers $H -Method Post }
function Money($x){ [math]::Round([decimal]$x,2) }

$M = Price
$p0 = Pos; $resv0 = if($p0){[int]$p0.reservedQuantity}else{0}; $qty0 = if($p0){[int]$p0.quantity}else{0}
Write-Host "market=$M  baseline pos.qty=$qty0 reserved=$resv0"

Step "1. Arm trailing-stop SELL (2 percent offset): reserves shares like a static sell-stop"
$r = Place @{ userId=$uid;stockId=$sid;quantity=2;side="Sell";entry="Market";stop="Trailing";currency=$ccy;trailOffset=2;trailIsPercent=$true }
Start-Sleep -Milliseconds 500
$oid = $r.placedOrder.orderId
$o = OrderById $oid
if ($o.status -eq "Pending" -and $o.stop -eq "Trailing") { Ok "armed Pending/Trailing #$oid" } else { Bad "status=$($o.status) stop=$($o.stop)" }
if ([decimal]$o.trailOffset -eq 2 -and ($o.trailIsPercent -eq $true)) { Ok "trail offset 2 percent persisted" } else { Bad "trailOffset=$($o.trailOffset) isPct=$($o.trailIsPercent)" }
$wm0 = [decimal]$o.trailWatermark
if ($wm0 -gt 0) { Ok "watermark seeded = $wm0 (market $M)" } else { Bad "watermark not seeded ($($o.trailWatermark))" }
$expStop0 = Money ($wm0 * 0.98)
if ((Money $o.stopPrice) -eq $expStop0) { Ok "effective stop = watermark*0.98 = $($o.stopPrice)" } else { Bad "stopPrice=$($o.stopPrice) expected $expStop0" }
$resv1 = [int](Pos).reservedQuantity
if ($resv1 -eq $resv0 + 2) { Ok "reserved 2 shares ($resv0 to $resv1)" } else { Bad "reserved $resv0 to $resv1 (expected +2)" }

Step "2. Ratchet + throttled flush: push price up, watermark must rise monotonically and persist"
for ($i=0; $i -lt 6; $i++){ try { Place @{ userId=$uid;stockId=$sid;quantity=1;side="Buy";entry="Market";stop="None";currency=$ccy;buyBudget=(Money ($M*5)) } | Out-Null } catch {} ; Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 5
$o2 = OrderById $oid
$wm1 = [decimal]$o2.trailWatermark
if ($wm1 -ge $wm0) { Ok "watermark monotonic and persisted: $wm0 to $wm1" } else { Bad "watermark dropped $wm0 to $wm1" }
$expStop1 = Money ($wm1 * 0.98)
if ((Money $o2.stopPrice) -eq $expStop1) { Ok "persisted effective stop tracks watermark = $($o2.stopPrice)" } else { Bad "stopPrice=$($o2.stopPrice) expected $expStop1" }
if ($wm1 -gt $wm0) { Ok ("watermark actually ratcheted up by " + [math]::Round($wm1-$wm0,2)) } else { Write-Host "  NOTE  price did not rise this run; ratchet not exercised (monotonic still held)" -ForegroundColor Yellow }

Step "3. Cancel: releases the share reservation"
Cancel $oid | Out-Null; Start-Sleep -Milliseconds 500
if ((OrderById $oid).status -eq "Cancelled") { Ok "cancelled" } else { Bad "not cancelled" }
$resvEnd = [int](Pos).reservedQuantity
if ($resvEnd -eq $resv0) { Ok "reservation released to baseline ($resv0)" } else { Bad "reserved=$resvEnd != baseline $resv0" }

Step "4. Cleanup: sell back any shares acquired while pushing the price up"
$qtyNow = [int](Pos).quantity
if ($qtyNow -gt $qty0) {
  $excess = $qtyNow - $qty0
  Place @{ userId=$uid;stockId=$sid;quantity=$excess;side="Sell";entry="Market";stop="None";currency=$ccy } | Out-Null
  Start-Sleep -Milliseconds 600
}
$qtyEnd = [int](Pos).quantity
if ($qtyEnd -eq $qty0) { Ok "position net-neutral ($qty0)" } else { Bad "pos.qty=$qtyEnd != baseline $qty0" }

$col = if($fail -eq 0){"Green"}else{"Red"}
Write-Host "`n===== P5 RESULT: $pass passed, $fail failed =====" -ForegroundColor $col
