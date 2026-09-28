using DocGen.Contracts;

namespace DocGen.Cards;

// One Options class per LLM call site (dotnet-llm-http-config convention): each has its own section,
// named HttpClient, model and system prompt (default literal here, overridable from appsettings.json).
// PromptVersion is part of the card hash — bump it when the prompt changes meaningfully.
// MaxRetries = extra attempts after a response fails validation (the error list is sent back to the model).

public class HandlerCardOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który opisuje logikę biznesową kodu .NET dla analityków i programistów.
        Zasady:
        - Używaj wyłącznie faktów z kontekstu. Nie dopowiadaj zachowań, których nie widać w kodzie.
        - Pisz po polsku, językiem biznesu, bez nazw klas w polach title, summary, triggers, mainScenario, alternativeScenarios, rules i effects.
        - slug: nazwa pliku PascalCase po angielsku, np. "RequestPayout".
        - Każdy guard z kontekstu (G1..Gn) poza tymi z wyjściem "propagate" musi trafić dokładnie raz: albo do guardIds jednej reguły w "rules", albo do "dismissed" z powodem (warunki techniczne, np. pusty identyfikator).
        - Kilka guardów opisujących ten sam warunek biznesowy (np. warunek w handlerze i filtr w repozytorium) łącz w jedną regułę.
        - Reguły numeruj 1..n w kolejności sprawdzania w kodzie. "onFail" to krótki skutek niespełnienia, np. "Błąd walidacji.", "Brak akcji, bez błędu.", "Błąd „brak numeru konta”.".
        - Scenariusz główny opisuje sytuację, w której wszystkie reguły są spełnione. Każda reguła z sensownym skutkiem daje scenariusz alternatywny z ruleRef "reguła N" albo "reguły N–M".
        - Linki do innych stron wstawiaj tokenami [[page:<id>]] albo [[page:<id>|etykieta]], tylko dla id z listy stron.
        - configMeanings: krótkie znaczenie każdego klucza konfiguracji z kontekstu.
        Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}

public class EntityCardOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który opisuje encje domenowe systemu po polsku, językiem biznesu.
        Na podstawie pól, statusów i przejść z kontekstu podaj: polską nazwę encji (name), krótki opis (description),
        dla każdej wartości statusu polską etykietę (label, np. "Oczekująca") i znaczenie (meaning), opis każdego pola (fields)
        oraz reguły biznesowe encji wynikające z warunków przejść (rules). Nie dopowiadaj faktów spoza kontekstu.
        Linki: [[page:<id>]] tylko dla id z listy stron. Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}

public class FlowCardOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który opisuje procesy biznesowe przechodzące przez kilka przypadków użycia.
        Na podstawie kroków z kontekstu podaj po polsku: tytuł procesu (title), slug pliku (litery ASCII, cyfry i myślniki, np. "Wyplata-po-rozliczeniu"),
        opis (description), krótkie etykiety dla KAŻDEGO węzła diagramu (nodes, te same id co w kontekście; pytanie w węźle decyzyjnym sformułuj tak,
        by odpowiedź "tak" oznaczała zatrzymanie procesu), wstęp do sekcji cichych zatrzymań (silentStopsIntro) oraz dla KAŻDEGO miejsca cichego zatrzymania,
        w tej samej kolejności i z tym samym "place", powód (reason) i gdzie widać ślad (trace). Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}

public class GlobalRuleCardOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który opisuje reguły globalne (pipeline behaviors MediatR) po polsku, językiem biznesu.
        Podaj tytuł (title), cel w jednym zdaniu (summary) i punkty opisujące działanie (behaviour) wyłącznie na podstawie kodu z kontekstu.
        Linki: [[page:<id>]] tylko dla id z listy stron. Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}

public class ModuleSummaryOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który streszcza moduł systemu po polsku. Widzisz tylko tytuły i streszczenia stron modułu.
        Podaj tytuł modułu (title) i streszczenie jego odpowiedzialności w 2–4 zdaniach (summary). Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}

public class SystemSummaryOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który streszcza cały system po polsku. Widzisz tylko streszczenia modułów.
        Podaj tytuł (title) i streszczenie systemu w 3–5 zdaniach (summary). Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}

public class GlossaryOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";
    public string PromptVersion { get; set; } = "1";
    public int MaxRetries { get; set; } = 1;

    public string SystemPrompt { get; set; } = """
        Jesteś analitykiem, który tworzy słownik pojęć biznes ↔ kod po polsku.
        Dla nazw z listy kandydatów zaproponuj polskie pojęcie biznesowe (term) i jednozdaniowy opis (description).
        codeName przepisuj dokładnie z listy; pomiń kandydatów bez znaczenia biznesowego. Odpowiadasz wyłącznie JSON-em zgodnym ze schematem.
        """;
}
