# Inventory

The language for describing stock and where it is kept.

## Language

**Inventory**:
A named collection of stock and locations with one Owner and zero or more Editors and Viewers. A person may participate in multiple inventories.
_Avoid_: Account, personal inventory

**Participant**:
A person from the owning organization's membership who may hold an Inventory role. The same Participant is recognized across every channel.
_Avoid_: Account, channel user

**Active Inventory**:
The Inventory currently selected by one Participant within one conversation. It is a conversational convenience and never grants access.
_Avoid_: Default inventory, current account

**Inventory Recovery Administrator**:
A tenant-level operator allowed only to transfer ownership of an Inventory whose Owner is no longer an active Participant. This role grants no access to stock.
_Avoid_: Inventory administrator, superuser

**Stock Entry**:
A stable record representing interchangeable units of one kind of thing in an Inventory. It references one Unit and optionally one Location.
_Avoid_: Item, asset, object

**Note**:
Optional free text attached to a Stock Entry for distinctions not represented by its name, Unit, or Location. It does not determine Equivalent Stock.
_Avoid_: Attribute, tag

**Equivalent Stock**:
Stock with the same normalized name, Unit, and optional Location in the same Inventory. Equivalent stock is represented by one Stock Entry rather than duplicate entries.
_Avoid_: Duplicate item, lot

**Quantity**:
A non-negative decimal amount paired with a Unit.
_Avoid_: Count

**Unit**:
An Inventory-owned controlled measure referenced by a Stock Entry. Every Inventory has the reserved `each` Unit with the aliases `piece`, `pieces`, `pc`, and `pcs`. A Unit has a stable identity, canonical name, and optional spoken aliases; different Units are never automatically converted.
_Avoid_: Quantity type

**Location**:
A stable, flat named place used to distinguish where stock is kept within an Inventory. Its name is unique case-insensitively within that Inventory. Unlocated stock has no Location reference.
_Avoid_: Site, hierarchical location path

**Retire Unit**:
An Owner-only withdrawal of an unused Unit from future matching and assignment. Its stable identity remains for prior references and audits.
_Avoid_: Remove Unit, delete Unit

**Retire Location**:
An Owner-only withdrawal of an unused Location from future matching and assignment. Its stable identity remains for prior references and audits.
_Avoid_: Remove Location, delete Location

**Owner**:
The single participant who controls an Inventory, its membership, and retirement of unused reference data.
_Avoid_: Administrator

**Editor**:
A participant allowed to view and mutate an Inventory's stock and administer its non-destructive reference data.
_Avoid_: Contributor

**Viewer**:
A participant allowed to view an Inventory's stock without mutating it.
_Avoid_: Reader

**On-hand Stock**:
Stock Entries whose Quantity is greater than zero. Zero-quantity Stock Entries remain part of the Inventory but are omitted from ordinary on-hand views.
_Avoid_: Active items

**Add**:
A mutation that increases a Stock Entry's Quantity, creating the equivalent Stock Entry when none exists.
_Avoid_: Increment, receive

**Remove**:
A mutation that decreases a Stock Entry's Quantity. It is rejected when the requested amount exceeds the Quantity on hand.
_Avoid_: Delete, consume

**Set**:
A mutation that replaces a Stock Entry's Quantity with an exact non-negative value.
_Avoid_: Adjust

**Move**:
A mutation that transfers some or all Quantity from one Location to another. Equivalent stock at the destination is merged.
_Avoid_: Relocate, transfer

**Rename**:
A mutation that changes a Stock Entry's name. If the new name creates Equivalent Stock, the entries are merged.
_Avoid_: Retitle

**Forget**:
A confirmed mutation that permanently removes a zero-quantity Stock Entry.
_Avoid_: Delete, remove

**Match**:
A Stock Entry whose name and stated attributes satisfy a conversational reference. A mutation requires one Match unless the user explicitly names a plural scope.
_Avoid_: Guess, best match

**Normalized Name**:
A Stock Entry name compared without case differences, leading or trailing whitespace, or repeated internal whitespace. Singulars, plurals, and synonyms remain distinct names.
_Avoid_: Canonical product name

**List**:
A query returning Stock Entries filtered by Inventory, Location, name, or on-hand state. It returns On-hand Stock unless zero-quantity stock is explicitly requested.
_Avoid_: Browse

**Find**:
A query resolving matching Stock Entries and their details from a conversational reference. An ambiguous reference returns multiple candidates for clarification rather than selecting one.
_Avoid_: Search, lookup

**Initial Import**:
A confirmed, atomic creation of exact starting Stock Entries in an Inventory that has no Stock Entries. Equivalent Stock within the import is merged before creation.
_Avoid_: Seed, migration, bulk adjustment
