"""Supported FF Logs job metadata for offline tuning tools."""

from __future__ import annotations


JOBS = {
    "PLD": {"fflogs": "Paladin", "role": "tank", "profile": "common"},
    "WAR": {"fflogs": "Warrior", "role": "tank", "profile": "common"},
    "DRK": {"fflogs": "Dark Knight", "role": "tank", "profile": "common"},
    "GNB": {"fflogs": "Gunbreaker", "role": "tank", "profile": "common"},
    "WHM": {"fflogs": "White Mage", "role": "healer", "profile": "common"},
    "SCH": {"fflogs": "Scholar", "role": "healer", "profile": "common"},
    "AST": {"fflogs": "Astrologian", "role": "healer", "profile": "common"},
    "SGE": {"fflogs": "Sage", "role": "healer", "profile": "common"},
    "MNK": {"fflogs": "Monk", "role": "melee", "profile": "common"},
    "DRG": {"fflogs": "Dragoon", "role": "melee", "profile": "common"},
    "NIN": {"fflogs": "Ninja", "role": "melee", "profile": "common"},
    "SAM": {"fflogs": "Samurai", "role": "melee", "profile": "common"},
    "RPR": {"fflogs": "Reaper", "role": "melee", "profile": "common"},
    "VPR": {"fflogs": "Viper", "role": "melee", "profile": "common"},
    "BRD": {"fflogs": "Bard", "role": "physical_ranged", "profile": "common"},
    "MCH": {"fflogs": "Machinist", "role": "physical_ranged", "profile": "common"},
    "DNC": {"fflogs": "Dancer", "role": "physical_ranged", "profile": "common"},
    "BLM": {"fflogs": "Black Mage", "role": "caster", "profile": "common"},
    "SMN": {"fflogs": "Summoner", "role": "caster", "profile": "common"},
    "RDM": {"fflogs": "Red Mage", "role": "caster", "profile": "common"},
    "PCT": {"fflogs": "Pictomancer", "role": "caster", "profile": "common"},
}


ACTOR_SUBTYPE_ALIASES = {
    "PLD": ("Gladiator",),
    "WAR": ("Marauder",),
    "DRK": (),
    "GNB": (),
    "WHM": ("Conjurer",),
    "SCH": ("Arcanist",),
    "AST": (),
    "SGE": (),
    "MNK": ("Pugilist",),
    "DRG": ("Lancer",),
    "NIN": ("Rogue",),
    "SAM": (),
    "RPR": (),
    "VPR": (),
    "BRD": ("Archer",),
    "MCH": (),
    "DNC": (),
    "BLM": ("BlackMage", "Thaumaturge"),
    "SMN": ("Arcanist",),
    "RDM": (),
    "PCT": (),
}


RANKING_SPEC_NAMES = {
    "PLD": "Paladin",
    "WAR": "Warrior",
    "DRK": "DarkKnight",
    "GNB": "Gunbreaker",
    "WHM": "WhiteMage",
    "SCH": "Scholar",
    "AST": "Astrologian",
    "SGE": "Sage",
    "MNK": "Monk",
    "DRG": "Dragoon",
    "NIN": "Ninja",
    "SAM": "Samurai",
    "RPR": "Reaper",
    "VPR": "Viper",
    "BRD": "Bard",
    "MCH": "Machinist",
    "DNC": "Dancer",
    "BLM": "BlackMage",
    "SMN": "Summoner",
    "RDM": "RedMage",
    "PCT": "Pictomancer",
}


def normalize_job(job: str) -> str:
    normalized = job.strip()
    upper = normalized.upper()
    if upper in JOBS:
        return upper

    folded = normalized.casefold()
    for short_name, metadata in JOBS.items():
        if metadata["fflogs"].casefold() == folded:
            return short_name

    raise ValueError(f"unsupported job: {job}")


def fflogs_job_name(job: str) -> str:
    return JOBS[normalize_job(job)]["fflogs"]


def fflogs_ranking_spec_name(job: str) -> str:
    return RANKING_SPEC_NAMES[normalize_job(job)]


def fflogs_actor_subtypes(job: str) -> tuple[str, ...]:
    normalized = normalize_job(job)
    return (fflogs_job_name(normalized), *ACTOR_SUBTYPE_ALIASES[normalized])


def is_supported_job(job: str) -> bool:
    try:
        normalize_job(job)
    except ValueError:
        return False
    return True


def supported_jobs() -> list[str]:
    return list(JOBS.keys())
