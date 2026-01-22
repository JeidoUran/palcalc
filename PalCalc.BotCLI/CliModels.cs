using System;
using System.Collections.Generic;

sealed class ContainerInfo
{
    public string Kind = "Unknown";       // Palbox / PlayerParty / Base / ViewingCage / MarketStall / Unknown
    public string? OwnerName;             // nom joueur/guilde si on peut
    public string? OwnerId;               // guid string utile en debug
    public string? BaseId;                // base id si Base/Cage
    public (float X, float Y, float Z)? Pos;

    public int? MaxEntries;               // taille conteneur (RawPalContainerContents.MaxEntries)
}

// Représente un "pal" (ou perso) qu'on sort de la save
sealed class QuickChar
{
    public bool IsPlayer;
    public string? NickName;
    public string? CharacterId;
    public Guid? Owner;
    public string? Gender;
    public int Level;

    public Guid? InstanceId;

    public Guid? ContainerId;
    public int SlotIndex;

    // si true => ce pal ne vient pas du Level container map (ex: DPS)
    public string? VirtualContainerKind;      // "DimensionalPalStorage"
    public string? VirtualContainerOwnerName; // "Selene"

    public int? TalentHp;
    public int? TalentShot;
    public int? TalentMelee;
    public int? TalentDefense;

    public List<string> Passives = new();
}
