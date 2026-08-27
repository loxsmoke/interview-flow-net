<div class="search-warning">⚠️ <strong>Web search unavailable</strong> This report was generated without live web results. Details may be outdated — verify key claims before the interview.</div>

# 🛡️ Company Analysis Report: Acme Inc.

Acme builds developer tooling for logistics teams.
This line follows a single newline, so it must render as a new line (breaks: true).
**Bold text is weight 600**, *italic is italic*, and ***bold-italic combines both***. Inline code like `queue_manager.py` gets a slate chip.

## Confidence assessment

- [VERIFIED] Series C closed in 2025
- [LIKELY] Revenue between $40M and $60M
- [SPECULATIVE] Planning a European expansion
- Priorities: [HIGH] platform reliability, [MEDIUM] AI features, [LOW] rebranding

## Interview process

| Stage | Format | Duration | Signal |
|---|---|---|---|
| Screen | Recruiter call | 30 min | [HIGH] |
| Technical | Live coding | 60 min | [HIGH] |
| System design | Whiteboard | 45 min | [MEDIUM] |
| Values | Panel | 45 min | [LOW] |

### Sources

1. Careers page — [acme.example.com/careers](https://acme.example.com/careers)
2. Engineering blog — [blog.acme.example.com](https://blog.acme.example.com)

> Tip: reference their Q3 reliability incident when discussing on-call culture.
> It shows you did your homework.

## Team structure

```mermaid
graph TD
    CEO --> CTO
    CTO --> Platform["`- Runs the core API
- Owns reliability`"]
    CTO --> DevEx
    subgraph Product Org
    PM --> Design
```

## Prep checklist

1. Re-read the job posting
2. Practice the pitch
   - 10-second version
   - 60-second version

```python
def fit_score(candidate, role):
    return sum(s.weight for s in candidate.skills if s in role.requirements)
```

Final note with a bare autolink: https://www.anthropic.com and normal text after it.

---

# 🛡️ Company Analysis Report: Meridian Systems

**Date:** August 26, 2026
**Target Role:** Staff Software Engineer
**Overall Confidence:** [HIGH]

These bold-key metadata lines are separated by single newlines and must each render on their own line (breaks: true), exactly like the reports the original app stores.

---

## 1. Company Overview

Meridian is not a bank, but a **payments network** (a "network of networks"). It provides the rails that move money between the cardholder, the merchant, and the issuer.

*   **Business Model:** Primarily a **toll-booth model** — service fees based on volume, data processing fees, and cross-border fees. [VERIFIED]
*   **Scale Indicators:**
    *   65,000+ transactions per second at peak. [VERIFIED]
    *   Presence in 200+ countries. [REPORTED]
    *   80M+ merchants and 15k+ financial institutions. [LIKELY]
*   **Growth:** Value-added services revenue grew ~24% in FY2026. [SPECULATIVE]

## 2. Engineering Culture

*   **The modernization tension:** a visible divide between the legacy side (mainframe, `C++`, heavy compliance) and the cloud-native side (`Java`, `Spring Boot`, GKE). This role sits firmly in the latter. [LIKELY]
*   **Daily work life:** high emphasis on **compliance (PCI-DSS, SOC2)**. Engineers cannot "move fast and break things"; they must "move fast with extreme safety." [VERIFIED]
*   **Review patterns:** stability and benefits praised; complaints about "corporate bureaucracy" and slow approval cycles. [LOW] priority concern.
