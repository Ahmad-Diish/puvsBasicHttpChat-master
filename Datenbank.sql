CREATE TABLE IF NOT EXISTS badwords (
    id SERIAL PRIMARY KEY, -- Automatische ID für jedes Wort
    word TEXT NOT NULL UNIQUE -- Das zu filternde Wort, muss eindeutig sein
);

-- Beispiel-Daten einfügen
INSERT INTO badwords (word) VALUES
('kaka'),
('scheiße'),
('Arsch'),
('Arschloch'),
('Blödmann'),
('Dummkopf'),
('Drecksack'),
('Fick dich'),
('Hurensohn'),
('Idiot'),
('Scheiße'),
('Wichser'),
('Pisser'),
('Mistkerl'),
('Penner'),
('Schwein'),
('Sackgesicht'),
('Trottel'),
('Vollidiot'),
('Dreckskerl'),
('Sau'),
('Bastard')
ON CONFLICT DO NOTHING; -- Verhindert Duplikate beim erneuten Einfügen

CREATE TABLE benutzer(
    id SERIAL NOT NULL,
    sender varchar(255) NOT NULL,
    sender_color varchar(50),
    PRIMARY KEY(id)
);

CREATE TABLE chat(
    id SERIAL NOT NULL,
    content text NOT NULL,
    timestamp timestamp without time zone NOT NULL,
    sender_id integer NOT NULL,
    PRIMARY KEY(id),
    CONSTRAINT chat_sender_id_fkey FOREIGN key(sender_id) REFERENCES benutzer(id)
);