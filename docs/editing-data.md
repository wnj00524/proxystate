# Reading and editing the data files

Proxy State keeps the people, places, jobs, traits, groups, and activities used
by the simulation in the `data` folder. You can change that content without
changing the program itself. This guide explains what you are looking at and
how to make safe changes. You do not need to know C#.

## Before you edit

The files use JSON, a text format with a few punctuation rules:

* `{` and `}` hold one item and its details.
* `[` and `]` hold a list of items.
* A name and its value are separated by a colon, as in `"name": "Brave"`.
* List items and details are separated by commas. Do not add a comma after the
  last item in a list or object.
* Words must be inside straight double quotes. Numbers, `true`, `false`, and
  `null` do not use quotes.
* JSON does not allow comments. Keep notes about a change outside these files.

Make one small change at a time. Keep a backup or use Git so that you can undo
the change. Preserve the existing spelling and capitalisation of field names.
After every change, run the check described in [Checking your work](#checking-your-work).

## Names, IDs, hashes, and references

Most entries have more than one kind of name:

* `name` is the text a person sees. It is safe to change.
* `id` is the short internal name used to connect entries in different files.
  It must be unique within its list. Use lower-case words separated by hyphens,
  following the existing examples.
* `hash`, `factionId`, and `bit` are unique numbers used to remember an entry.
  Never reuse one. Once an entry has been used in a saved game or released
  version, do not change its number.
* A reference is an ID written somewhere else. For example, the job value
  `"workplaceType": "office"` refers to locations whose `type` is `office`.
  If you rename an ID or type, find and update every reference to it.

Capitalisation matters for IDs and references. Copy an existing entry when
adding a similar one, then change only the values you understand.

## What each file controls

### `lod.json`: simulation detail rollout

This file controls the staged agent level-of-detail rollout. `enabled` turns on
the classification contract, while `tier3Enabled` must remain `false` until the
coarse routine implementation is complete. `tier2.decisionIntervalMinutes` and
`tier3.shardCount` must be positive whole numbers. The supported `relatedBy`
values are exactly `social`, `networkSupervisor`, and `networkDirectReport`;
each must appear once. The only supported `demotionPolicy` is `endOfDay`.

These values are validated and compiled when content loads. Do not add new
relationship or policy words without implementing their runtime meaning first.

### `agent-schema.json`: agent numbers

The `attributes` list describes the numerical qualities every generated agent
has, such as perception, fatigue, or wealth.

* `id` is the attribute's unique internal name.
* `min` and `max` are the lowest and highest allowed values.
* `average` is the centre of the values given to newly generated agents. It
  must be between `min` and `max`.

Changing these ranges changes newly generated agents and also changes how an
attribute is scaled in activity choices. Renaming or removing an attribute can
break an activity in `actions.json` or a built-in simulation feature. Search
the whole project for the ID before doing either.

### `traits.json`: yes-or-no personality traits

Each generated agent may have each trait.

* `name` is the displayed label.
* `prevalence` is the chance of receiving the trait: `0` means never, `1` means
  always, and `0.25` means about one agent in four.
* `bit` must be a unique power of two: `1`, `2`, `4`, `8`, `16`, and so on.
  Use the next unused value when adding a trait; do not renumber existing ones.

An action may name a trait in a `traitModifiers` list. Renaming a trait ID also
requires updating those references. Some existing traits, such as `paranoid`,
also have special meaning in the current simulation code, so search before
renaming or removing them.

### `factions.json`: political sides

Each entry is a faction available when agents are generated. `name` is the
displayed label. Both `id` and `factionId` must be unique. Use a new, unused
whole number for `factionId`; do not renumber a faction that has already been
used.

### `politics.json`: elections and turnout

The city politics file controls the recurring election schedule and turnout
model:

* `electionIntervalDays` is the number of simulation days between elections.
  The first election occurs at the end of the first interval.
* `nominationDays` opens candidate registration for that many days immediately
  before election day. Candidates register by traveling to Town Hall.
* `pollOpeningMinute` and `pollClosingMinute` are minutes after midnight on
  election day. Voters travel to the configured `pollingLocationId`.
* `politicalEngagementAttribute` and `motivationAttribute` name attributes
  from `agent-schema.json` used to decide whether agents run and vote.
* Candidate and vote weights combine those attributes. Threshold and
  variation values represent different willingness to participate. Travel
  and work penalties reduce voting likelihood. Social pressure comes from the
  average political engagement of an agent's social contacts.

Each political job declares `selectionMethod`. Elected jobs receive the
highest vote total after faction preference and charisma select each ballot.
Appointed jobs name an elected `appointedByJobId`; that official fills the role
from applicants, favouring their faction and then applicant engagement and
charisma. Agents can hold only one political position at a time.

### `secret-states.json`: hidden states

These entries name covert states that can be shown in the development-only
inspector. There must always be exactly one entry with the ID `none` and hash
`0`; it is the starting state. Other entries need unique IDs and hashes (use a
new non-zero hash so it cannot be confused with the default). Adding an entry
makes the name available to the program, but does not by itself make agents
enter that state.

### `jobs.json`: jobs and working hours

Each entry describes one job:

* `workStartMinute` and `workEndMinute` count minutes after midnight. For
  example, `480` is 8:00 a.m. and `1020` is 5:00 p.m. The end must be later
  than the start and earlier than `1440` so the agent can commute home;
  overnight shifts cannot currently be described.
* `workDays` uses `1` for Monday through `7` for Sunday. Do not repeat a day.
* `workplaceType` must exactly match the `type` of at least one location in
  `world.json`. Agents with this job can be assigned to those locations.
* `sector` is `private` or `public` and identifies who employs the worker.
* `weeklyPay` is weekly pay in simulation credits. It must be zero or higher.
* `prestige` is the role's social standing from 1 (low) to 100 (highest).
  Prestige is independent of pay, so public service and political roles can
  have high standing without the highest salary.
* `selectionMethod` is `null` for ordinary work, `elected` for a ballot office,
  or `appointed` for an office filled by another elected official.
* `appointedByJobId` names the elected job allowed to appoint this role. It
  must be `null` for ordinary and elected jobs.

Jobs need unique IDs and hashes. A job also needs at least one working day.
Schedules can represent opening hours, shifts, evening meetings, and shorter
multi-day weeks, as long as each shift starts and ends within one day.

### `world.json`: places and routes

`locations` contains the places in the world. Every location needs a unique ID
and hash. Its `type` describes its purpose. Homes use `residential`; job
locations use the type named by a job's `workplaceType`. Other types, such as
`transit`, can be used to join routes.

`connections` contains travel links:

* `from` and `to` must be IDs from the `locations` list.
* `travelMinutes` must be greater than zero.
* A connection works in both directions, so do not add a reversed duplicate.

Make sure every home can reach every workplace that agents may need. A place
that is listed but disconnected from the rest of the map can leave an agent
unable to travel there.

### `networks.json`: families, friends, and companies

This file describes how the simulation groups agents. It has three lists:

1. `networkTypes` defines a kind of group and lists the role IDs allowed in it.
   `flat` means no one supervises anyone; `single-supervisor` means each person
   below the head has one supervisor. `maxNetworksPerAgent` limits how many
   groups of that type one agent can join. `seedsSocialGraph` makes every pair
   in each generated group reciprocal social peers; use it only when membership
   implies an ongoing social relationship.
2. `roles` defines the displayed roles and says which network type owns each
   role. A role must also appear in that type's `roles` list.
3. `generators` says how to create the groups. `home-location` groups people
   who share a home location; `work-location` groups people who share a work
   location; `global` partitions the whole town and uses anchor `0`. Minimum
   and maximum sizes set the permitted range. `sizeWeights`
   makes some sizes more likely than others: a larger positive weight means a
   size is chosen more often, not that it is guaranteed.

For a flat group, set `memberRole` and leave the three hierarchy roles `null`.
For a supervised group, set `rootRole`, `managerRole`, and `leafRole`, and leave
`memberRole` as `null`. `targetSpanOfControl` is the preferred number of direct
reports, `maximumSpanOfControl` is the hard limit, and `maximumDepth` is the
greatest number of levels below the head. The maximum group size must fit
within those limits.

`remainderHandling` controls what happens when the last few agents do not make
a normal-sized group. `create-undersized` creates a smaller final group;
`merge-into-previous` adds them to an earlier group when it can do so safely.
Because these settings depend on one another, copy the family generator for a
new flat group or the company generator for a new supervised group, then make
small changes and validate each one.

### `actions.json`: activities and choices

This is the most connected file. Each entry describes an activity an agent can
choose, where it happens, how attractive it is, and how it changes the agent.
Read an entry from top to bottom:

* `id`, `name`, and `hash` identify the choice. The nested `activity` gives the
  visible activity its own identity and unique hash.
* `baseUtility` is its starting appeal. A larger score makes the action more
  likely to win against other available actions.
* `eligibility` says when the action is allowed. It reads like a question tree:
  `and` means every item must be true, `or` means at least one must be true,
  and comparisons such as `less` compare their left and right values.
* `utilityInputs` adjust the appeal. `weight` controls the size and direction
  of an adjustment; a negative weight discourages the action. The `expression`
  supplies a value and the `curve` reshapes it. Curve points must be ordered by
  `x` and describe the output `y` for each input `x`.
* `traitModifiers` adds or subtracts appeal when the agent has the named trait.
* `controls` prevents rapid switching. The values are minutes for minimum
  commitment and cooldown, and score thresholds for switching and urgent
  interruption.
* `effects` changes attributes while the activity is being performed. A
  positive `perMinute` raises the value and a negative one lowers it. The
  attribute's minimum and maximum still apply. Optional `subject` is
  `initiator` or, for mutual activities, `participant`.
* `target` and `execution` say where and how it happens.
* Optional `participation` makes an entity activity mutual. It sets minimum and
  maximum shared duration, rejection cooldown, and the invitee's separately
  evaluated eligibility and utility.

The target and execution must be one of these matching pairs:

| Target | Execution | Meaning |
|---|---|---|
| `none` | `performHere` | Perform at the current place. |
| `none` | `wait` | Stay idle; used for the fallback choice. |
| `location` | `performAtLocation` | Travel to and perform at home, work, or the current place. |
| `entity` | `performWithEntity` | Find another agent, travel to them, and perform together. |

There must be exactly one fallback action. It uses `"fallback": true`, a
`none` target, and the `wait` executor. The simulation chooses it only when no
ordinary action is available.

Actions use facts such as the time, an agent attribute, or a target's affinity.
Facts and expression rules are intentionally strict. For a first edit, change a
display name, score, time, effect, or existing curve rather than inventing a
new question tree. For new behaviour, copy the closest complete action and use
the [advanced intent authoring guide](intent-authoring.md) as a reference.

Entity queries may use `social`, `network-member`, `network-supervisor`, or
`network-direct-report`. Every network relation requires `networkType`; social
must omit it. Supervisor/report queries require a `single-supervisor` type.
Target scoring may read `target.attribute.<id>` and `target.affinity`; network
members without a social edge use neutral affinity.

## A safe editing example

To make the `greedy` trait less common:

1. Open `data/traits.json` in a plain-text editor.
2. Find the entry whose ID is `greedy`.
3. Change only `"prevalence": 0.35` to, for example,
   `"prevalence": 0.20`.
4. Save the file. Do not change its ID or bit.
5. Validate the whole data folder.

To add content, copy a whole entry including its braces, place a comma between
it and its neighbour, and then assign every required unique ID or number. Never
copy an entry and leave its old hash, faction ID, or trait bit in place.

## Checking your work

### `factions.json`: leadership and strategy

Each faction names elected `leaderJobId` and non-elected `activistJobId` jobs,
one `metaGoal`, and a set of `goals`. A goal selects an action (`recruit`,
`organize`, `campaign`, `office-seeking`, or `govern`), tracks one measure
(`members`, `organization`, `support`, or `control`), and may list prerequisite
goal IDs. Leaders pursue the highest-priority unfinished goal whose
prerequisites have completed. Keep faction IDs in sync with each faction job's
`factionId`; leader jobs use `factionRole: leader`, and activist jobs use
`factionRole: activist`. Goal IDs must be unique across all factions.

### `lod.json`: Tier 3 routines

Tier 3 routine content is a timetable template, not a list of hard-coded game
actions. `workday` must include one `jobWork`, `commuteToWork`, and
`commuteHome` segment; `nonWorkday` cannot contain those kinds. Each routine
has one fixed segment with `fillRemaining: true`, which occupies all minutes
not reserved by the job, commute, or other fixed segments. Each segment uses a
unique stable `id`, an existing action `intent`, `home` or `work` location, and
an `initiator` or `participant` `effectRole` that exists on that action.

`traitDurationModifiers` adjusts a named segment by whole minutes for agents
with a named trait. Both names are validated when content loads. Validation
errors identify a path such as `lod.json:tier3.workday[2].intent`; correct that
specific authored reference rather than adding a code special case.

From the project folder, run:

```text
dotnet run --project ProxyState.csproj -- --validate-content data
```

A successful check reports how many activities were validated and exits
without opening the game window. An error names the file and usually the entry
or field that needs attention. Fix the first error and run the command again;
one punctuation mistake can cause several later-looking errors.

The check confirms that the files can be read and that their references and
limits agree. It cannot judge whether a change is fun or balanced. After the
check passes, run the simulation and observe the changed content. Run the full
automated test suite before sharing a change:

```text
dotnet test ProxyState.sln
```
