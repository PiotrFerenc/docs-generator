---
title: "Orders / Rozliczenie zlecenia skupu"
page_id: "T:Orders.Application.SettleOrderHandler"
generated: true
source_commit: a1b2c3d
---

# Rozliczenie zlecenia skupu

> Strona generowana automatycznie z kodu. Nie edytuj jej. Uzupełnienia dodawaj w `SettleOrder.notes.md`.

## Cel

Zamyka zlecenie skupu po zaakceptowaniu wyceny przez klienta i uruchamia wypłatę pieniędzy.

## Kiedy się uruchamia

- Ręcznie: specjalista od wyceny rozlicza zlecenie, gdy klient zaakceptował zaproponowaną cenę.

## Scenariusze

**Scenariusz główny:** zlecenie istnieje, jest wycenione i ma zaakceptowaną cenę większą od zera. System oznacza zlecenie jako *Rozliczone*, zapisuje datę rozliczenia i w tle uruchamia wypłatę.

**Scenariusze alternatywne:**

- **Zlecenie nie istnieje** (reguła 1): błąd, nic się nie zmienia.
- **Zlecenie nie jest w stanie „Wycenione”** (reguła 2), np. już rozliczone albo anulowane: błąd, nic się nie zmienia.
- **Brak zaakceptowanej ceny albo cena nie jest większa od zera** (reguła 3): błąd, nic się nie zmienia.

Specjalista dostaje potwierdzenie rozliczenia, zanim wypłata zostanie zlecona. **Udane rozliczenie nie oznacza, że wypłata powstała** (patrz [Start wypłaty po rozliczeniu zlecenia](../../Payments/use-cases/StartPayoutAfterSettlement.md)).

## Reguły biznesowe

| # | Reguła | Skutek niespełnienia |
|---|---|---|
| 1 | Zlecenie musi istnieć. | Błąd „nie znaleziono zlecenia”. |
| 2 | Zlecenie musi mieć status *Wycenione*. | Błąd „zlecenie nie może zostać rozliczone”. |
| 3 | Zlecenie musi mieć zaakceptowaną cenę większą od zera. | Błąd „brak zaakceptowanej ceny”. |

## Efekty

- Zmienia status zlecenia na *Rozliczone* i zapisuje datę rozliczenia.
- Po zapisie uruchamia [Start wypłaty po rozliczeniu zlecenia](../../Payments/use-cases/StartPayoutAfterSettlement.md) (oraz obsługę bonów w module bonów, poza zakresem przykładu).

## Obowiązujące reguły globalne

- [Walidacja komend](../../../global-rules/ValidationBehavior.md)

## Proces

- [Wypłata po rozliczeniu zlecenia skupu](../../../flows/Wyplata-po-rozliczeniu.md)

---

## Szczegóły techniczne

| | |
|---|---|
| Handler | `Orders.Application.SettleOrderHandler` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Application/SettleOrder.cs#L18)) |
| Komenda | `SettleOrderCommand(OrderId)` → `Result` |
| Walidator | `SettleOrderCommandValidator` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Application/SettleOrder.cs#L10)) |
| Wyzwalacz | `POST /orders/{orderId}/settle` ([kod](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Api/OrderEndpoints.cs#L11)); błąd → HTTP 422 z komunikatami |
| Publikuje | `OrderSettledEvent(OrderId, CustomerId, Amount, PayoutMethod)` (z `Order.Settle`, po commicie) |
| Zależności | `IOrderRepository`, `TimeProvider` |

**Reguły w kodzie**

| # | Warunek | Wyjście | Miejsce |
|---|---|---|---|
| 1 | `order is null` | `OrderNotFoundError` | [SettleOrder.cs:24](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Application/SettleOrder.cs#L24) |
| 2 | `Status != OrderStatus.Valued` w `Order.Settle` | `OrderNotValuedError` | [Order.cs:31](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Domain/Order.cs#L31) |
| 3 | `AcceptedPrice is null or <= 0m` w `Order.Settle` | `MissingAcceptedPriceError` | [Order.cs:34](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Domain/Order.cs#L34) |

Pominięte w części biznesowej (warunki techniczne): `RuleFor(x => x.OrderId).NotEmpty()` ([SettleOrder.cs:14](https://git.example/skup/-/blob/a1b2c3d/src/Orders.Application/SettleOrder.cs#L14)).

**Dane**

| Operacja | Tabela | Kolumny |
|---|---|---|
| odczyt | `orders` | `id`, `status`, `accepted_price`, `customer_id`, `payout_method` |
| zapis (update) | `orders` | `status` → `Settled`, `settled_at` |

## Uwagi zespołu

(treść dołączana z `SettleOrder.notes.md`, jeśli istnieje)
