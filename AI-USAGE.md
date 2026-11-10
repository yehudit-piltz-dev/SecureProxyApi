# AI Usage Declaration

## Tools

| Tool | Used for |
|---|---|
| **Claude Code** (Anthropic, model Claude Opus 5.5), in Visual Studio Code | Interpreting the brief, design, writing the code, migrations, tests and documentation, running builds, tests and manual checks, and explaining the code to me |
| **Visual Studio 2022** | Running and debugging the solution, and inspecting the database (SQL Server Object Explorer) |

## How I used AI

- **Understanding the brief.** Claude Code pointed out where the brief was unclear: the open `json` value in the
  example request (section 4), and a reference in the first version of the brief to a supplied repository. I asked
  the company about the repository, and they confirmed the project is built from scratch. My reading of the
  request contract is stated as an assumption in DESIGN-NOTES.md.
- **Design and implementation.** Claude Code proposed the architecture and wrote the code, the EF Core model and
  migration, the admin API, the tests and the documents, in stages. After each stage it built the solution, ran it
  against LocalDB and sent valid and malicious requests to check the behavior.
- **Decisions I made.** The delivery plan and its stages; documenting caller authentication as out of scope
  rather than implementing it (DESIGN-NOTES.md, section 11); clarifying the brief with the company.
- **Learning and review.** I ran the solution in Visual Studio, sent requests through Swagger and the `.http`
  file, checked the audit records in the database, and walked through the request pipeline in the debugger,
  with Claude Code explaining each step.
- **Defects.** The defects in DEFECT-REPORT.md came up while building and running the solution, in Claude Code's
  verification runs and code reviews.

Commits made with Claude Code carry a `Co-Authored-By: Claude` trailer.

## Responsibility

I take responsibility for the submitted solution. I reviewed the design decisions recorded in DESIGN-NOTES.md and
can explain, trace and modify the code.
