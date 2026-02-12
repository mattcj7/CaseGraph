SourceLocator Conventions

SourceLocator must be stable and precise enough to locate the underlying data.

UFDR

Format: ufdr:<internalPath>#<artifactId|xpath>

Examples:

ufdr:report.xml#artifact:12345

ufdr:report.xml#xpath:/Report/Artifacts/Messages/Message[812]

XLSX

Format: xlsx:<fileName>#<sheet>:R<row>

Optional: include a column reference if needed

Examples:

xlsx:export.xlsx#Messages:R42

xlsx:export.xlsx#CallLog:R18:C7

ZIP-HTML

Format: html:<relativePath>#<stableId>

stableId can be: message id, element id, or deterministic index path

Examples:

html:messages/inbox/thread1.html#msg-8821

html:profile/index.html#element:main

PLIST

Format: plist:<relativePath>#<keypath>

Examples:

plist:Library/Preferences/app.plist#Root.Locations[12].Latitude

plist:cache.plist#Root.items[3].timestamp

CSV

Format: csv:<relativePath>#row:<n>

Example:

csv:geo/locations.csv#row:381

JSON

Format: json:<relativePath>#<json-pointer>

Example:

json:takeout/location.json#/timelineObjects/128/placeVisit/location/latitudeE7
