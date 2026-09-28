# docgen

Generator dokumentacji biznesowej (po polsku, Markdown) z kodu .NET w architekturze Clean Architecture
(MediatR, FluentValidation, FluentResults, EF Core). Wynik publikuje do Confluence i indeksuje w Qdrant,
żeby dało się zadawać pytania typu „dlaczego klient nie dostał wypłaty?”.

## Etapy

| Etap | Komenda | Co robi | Wynik |
|---|---|---|---|
| index | `docgen index` | Roslyn: punkty wejścia (handlery, walidatory, behaviory, endpointy), guardy w kolejności wykonania (`Result.Fail`, ciche `Result.Ok`, walidacja, wyjątki), krawędzie (send/publish, odczyty/zapisy encji, konfiguracja), encje | `<WorkDir>/index.json` |
| cards | `docgen cards` | Karty stron: część deterministyczna z indeksu + proza z LLM. Cache po `CardHash` (wejścia z indeksu + wersja promptu + model) — niezmienione karty nie idą ponownie do LLM | `<WorkDir>/cards.json` |
| render | `docgen render` | Karty → strony Markdown (przypadki użycia, encje, procesy, reguły globalne, moduły, system, słownik) | `<OutputDir>/**.md` + `.manifest.json` |
| publish | `docgen publish [--dry-run]` | Synchronizacja `<OutputDir>` z Confluence (drzewo stron wg manifestu) | strony w przestrzeni `SpaceKey` |
| search | `docgen search-index`, `docgen ask "<pytanie>"` | Indeksowanie stron w Qdrant, odpowiedzi z cytatami | kolekcja Qdrant, odpowiedź w konsoli |

Status karty (`Meta.Status`): `ok`, `needs_review` (do przejrzenia przez człowieka), `offline` (wygenerowana bez LLM).

## Komendy

```
docgen [--config appsettings.json] [--out <katalog>] <command>

  index                      Roslyn -> <WorkDir>/index.json
  check-index <expected>     porównanie index.json z oczekiwanymi guardami/krawędziami (exit 1 gdy czegoś brak)
  cards                      -> <WorkDir>/cards.json
  render                     -> <OutputDir>/ + .manifest.json
  publish [--dry-run]        <OutputDir> -> Confluence
  search-index               <OutputDir> -> Qdrant
  ask "<pytanie>"            retriever + reranker + odpowiedź LLM
  run [--publish] [--dry-run] [--search]
                             index + cards + render (+ publish) (+ search-index)
```

Z repozytorium: `dotnet run --project src/DocGen.Cli -- <command>`.

### Zapis do Markdown

`render` (także w ramach `run`) zapisuje strony Markdown do `Generator:OutputDir` (domyślnie `docs-out/`)
razem z `.manifest.json`. Inny katalog docelowy, np. folder `docs/` w repozytorium aplikacji albo vault:

```bash
dotnet run --project src/DocGen.Cli -- --out ../moja-aplikacja/docs run
```

Zapisywane są tylko zmienione pliki. Usuwane są wyłącznie strony wygenerowane wcześniej (wymienione w starym
manifeście), więc ręcznie dodane pliki w tym katalogu zostają nietknięte. `--out` działa też dla `publish`
i `search-index` (czytają stamtąd strony).

## Konfiguracja

1. `appsettings.json` — cała konfiguracja, łącznie z adresami i kluczami LLM, **gitignored**.
   Utwórz go kopią `appsettings.Example.json` (szablon w repozytorium, puste `ApiKey`) i uzupełnij
   `BaseAddress` / `ApiKey` w sekcjach wywołań LLM. Ścieżki względne liczone są od katalogu tego pliku.
2. Zmienne środowiskowe `DOCGEN__<Sekcja>__<Klucz>` nadpisują plik, np. `DOCGEN__HandlerCard__ApiKey`,
   `DOCGEN__Qdrant__Url` (wygodne w CI).

Sekcje:

- `Generator` — `WorkDir` (index, karty, cache; domyślnie `.docgen`), `OutputDir`, `NotesDir`, `GlossaryFile`, `Language`.
- `Indexer` — `SolutionPath`, `Commit`, `MaxDepth`, `EntityNamespaceSuffixes`, `ColumnNaming`.
- Wywołania LLM/API — **każde ma własną sekcję i własny `HttpClient`**: `HandlerCard`, `EntityCard`, `FlowCard`,
  `GlobalRuleCard`, `ModuleSummary`, `SystemSummary`, `Glossary`, `Embeddings`, `Reranker`, `SearchAnswer`.
  Klucze: `BaseAddress`, `Model`, `TimeoutSeconds`, `ApiKey`, `Headers` (`{ApiKey}` jest podstawiane, np.
  `"Authorization": "Bearer {ApiKey}"`), `SystemPrompt` tam, gdzie wywołanie wysyła prompt systemowy
  (domyślna wartość jest w klasie `<Sekcja>Options`, plik ją nadpisuje).
  Każda sekcja = osobna klasa `<Sekcja>Options : HttpClientOptions` + nazwany `HttpClient` o tej samej nazwie,
  więc np. embeddingi mogą iść do lokalnego serwera, a karty do OpenAI — zmiana jednej sekcji w `appsettings.json`.
- `Render` — `RepoUrlTemplate` z `{commit}`, `{path}`, `{line}` dla linków do kodu.
- `Confluence` — `BaseAddress`, `SpaceKey`, `RootPageTitle`, `MermaidMode`, `ApiKey` (Basic: base64 z `email:api-token`).
- `Qdrant` — `Url`, `Collection`, `ApiKey` (nagłówek `api-key`).
- `Embeddings` — `POST embeddings` zgodny z OpenAI; `Dimensions` = rozmiar wektora w kolekcji.
- `Reranker` — `POST rerank` `{model, query, documents, top_n}`; odpowiedzi Jina/Cohere/vLLM
  (`{results:[{index, relevance_score}]}`) i HF TEI (`[{index, score}]`).
- `SearchAnswer` — `chat/completions` zgodne z OpenAI.

**Tryb offline:** pusty `BaseAddress` wyłącza dane wywołanie. Karty powstają wtedy deterministycznie
(status `offline`), embeddingi to haszowany bag-of-words, reranker zostawia kolejność z wyszukiwania,
a `ask` zamiast odpowiedzi LLM wypisuje najlepsze fragmenty z numerami `[n]`. Cały pipeline działa bez kluczy
(Qdrant jest potrzebny tylko do `search-index`/`ask`).

## Wyszukiwanie

- Chunk = jedna sekcja H2 strony (bez frontmattera, baneru „generowana automatycznie” i pustego miejsca na uwagi).
- Punkt ma deterministyczne id (UUID v5 z `pageId` + sekcji), wektor gęsty `dense` (Cosine) i rzadki `sparse`
  (haszowane tokeny z TF; IDF liczy Qdrant). Kolekcja zakładana jest automatycznie.
- Strony z niezmienionym hashem (i tym samym modelem embeddingów) są pomijane; strony usunięte z manifestu
  i nieaktualne sekcje są kasowane. Ponowne `search-index` bez zmian = 0 chunków.
- `ask`: embedding pytania → zapytanie hybrydowe (dense + sparse, fuzja RRF, 20 kandydatów) → reranker → top 5
  → LLM odpowiada wyłącznie na podstawie fragmentów, cytując je jako `[n]`.
- Zmiana `Embeddings:Dimensions` wymaga nowej kolekcji (zmień `Qdrant:Collection` albo usuń starą).

## Przykład

`sample/` to przykładowa solucja (skup zleceń + wypłaty). `tests/golden/` zawiera ręcznie napisane strony
wzorcowe, do których dąży wynik generatora. Numery linii w `sample/src` są przywoływane przez strony wzorcowe
i `tests/expected-index.json` — nie przesuwaj ich przy edycji przykładu.

```bash
dotnet run --project src/DocGen.Cli -- index
dotnet run --project src/DocGen.Cli -- check-index tests/expected-index.json
dotnet run --project src/DocGen.Cli -- run            # offline, bez kluczy
docker run -d -p 6333:6333 qdrant/qdrant
dotnet run --project src/DocGen.Cli -- search-index
dotnet run --project src/DocGen.Cli -- ask "Dlaczego klient nie dostał wypłaty?"
```

## Testy

```bash
dotnet test
```

Test end-to-end wyszukiwania używa lokalnego Qdrant (`http://localhost:6333`) i tymczasowej kolekcji
`docgen_test_<guid>`, którą po sobie usuwa. Gdy Qdrant nie odpowiada, test jest raportowany jako pominięty.

## CI

`ci/docs.sh` uruchamiany z katalogu głównego repozytorium **aplikacji** (przykłady: `ci/gitlab-ci.yml`,
`ci/azure-pipelines.yml`):

- `DOCGEN_HOME` (ścieżka do repo docgen, budowane w jobie) albo `docgen` w `PATH`; `DOCGEN_CONFIG` (domyślnie `appsettings.json`).
- Zawsze `docgen run`. Katalog `.docgen/` trzymaj w cache CI — niezmienione karty nie idą ponownie do LLM.
- Merge request: przy ustawionym `DOCS_COMMIT_DIR` strony są kopiowane do tego katalogu i wypisywane jest
  `git diff --stat`, więc recenzent widzi zmiany reguł biznesowych (skrypt niczego nie commituje).
- Main: `docgen publish` (`DOCGEN_PUBLISH=1`, domyślnie) i `docgen search-index` (`DOCGEN_SEARCH=1`, domyślnie wyłączone).
- Tryb wykrywany ze zmiennych GitLab/Azure, można wymusić `DOCGEN_MODE=mr|main`.
- Sekrety jako (maskowane) zmienne CI `DOCGEN__<Sekcja>__ApiKey`.

## Confluence

- Strony są w całości generowane — **ręczne edycje w Confluence zostaną nadpisane** przy następnym `publish`.
  Uzupełnienia pisz w `docs-notes/<Slug>.notes.md` (w repo); ich treść trafia do sekcji „Uwagi zespołu”.
  Ręczne hasła słownika trzymaj w `glossary.json` (`Manual = true`, nigdy nie są nadpisywane).
- `MermaidMode`: `macro` (makro aplikacji Mermaid z Marketplace), `code` (zwykły blok kodu),
  `image` (PNG z `mmdc`, wgrany jako załącznik).
- `publish --dry-run` pokazuje, co zostałoby utworzone/zmienione, bez zapisu.

## Ograniczenia

- Analiza statyczna: reguły wyliczane w runtime (refleksja, reguły trzymane w bazie, dynamiczne pipeline'y)
  nie są widoczne. Karty `needs_review` wymagają przejrzenia.
- Proza z LLM może być nieprecyzyjna — źródłem prawdy są tabele „Reguły w kodzie” z linkami do kodu.
- Wyszukiwanie offline (bag-of-words, słowa obcinane do 6 znaków jako prymitywny stemming) nadaje się do testów;
  do realnego użycia skonfiguruj `Embeddings`, `Reranker` i `SearchAnswer`.
- Sekcje stron nie są dzielone na mniejsze fragmenty — bardzo długa sekcja może przekroczyć limit modelu embeddingów.
