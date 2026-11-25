-- Schema v1: device inventory, connection history, volume events, hash-chained audit trail.
-- Timestamps are UTC ISO 8601 round-trip strings (they sort correctly as text).

CREATE TABLE devices (
    fingerprint_key   TEXT    NOT NULL PRIMARY KEY,
    confidence        TEXT    NOT NULL,
    vendor_id         INTEGER,
    product_id        INTEGER,
    hardware_serial   TEXT,
    vendor            TEXT,
    product           TEXT,
    revision          TEXT,
    model             TEXT,
    bus_type          TEXT    NOT NULL,
    size_bytes        INTEGER NOT NULL,
    first_seen_utc    TEXT    NOT NULL,
    last_seen_utc     TEXT    NOT NULL,
    connection_count  INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE connections (
    connection_id           TEXT    NOT NULL PRIMARY KEY,
    fingerprint_key         TEXT    NOT NULL REFERENCES devices (fingerprint_key),
    storage_instance_id     TEXT    NOT NULL,
    usb_parent_instance_id  TEXT,
    container_id            TEXT,
    disk_number             INTEGER NOT NULL,
    connected_utc           TEXT    NOT NULL,
    disconnected_utc        TEXT,
    end_reason              TEXT
) STRICT;

CREATE INDEX ix_connections_open ON connections (connection_id) WHERE disconnected_utc IS NULL;
CREATE INDEX ix_connections_connected ON connections (connected_utc);
CREATE INDEX ix_connections_device ON connections (fingerprint_key, connected_utc);

CREATE TABLE volume_events (
    id                INTEGER NOT NULL PRIMARY KEY,
    connection_id     TEXT    NOT NULL REFERENCES connections (connection_id) ON DELETE CASCADE,
    occurred_utc      TEXT    NOT NULL,
    kind              TEXT    NOT NULL,
    volume_guid_path  TEXT    NOT NULL,
    drive_letter      TEXT,
    label             TEXT,
    file_system       TEXT,
    volume_serial     INTEGER,
    capacity_bytes    INTEGER
) STRICT;

CREATE INDEX ix_volume_events_connection ON volume_events (connection_id);

-- Sequence is assigned by the writer (head + 1), never by AUTOINCREMENT, so gaps are evidence of deletion.
CREATE TABLE audit_events (
    sequence       INTEGER NOT NULL PRIMARY KEY,
    occurred_utc   TEXT    NOT NULL,
    event_type     TEXT    NOT NULL,
    connection_id  TEXT,
    device_key     TEXT,
    details        TEXT    NOT NULL,
    previous_hash  TEXT    NOT NULL,
    hash           TEXT    NOT NULL
) STRICT;

CREATE INDEX ix_audit_events_occurred ON audit_events (occurred_utc);

-- Last entry removed by retention purge; verification continues from it.
CREATE TABLE audit_anchor (
    id           INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    sequence     INTEGER NOT NULL,
    hash         TEXT    NOT NULL,
    created_utc  TEXT    NOT NULL
) STRICT;
