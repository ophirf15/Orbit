-- Explicit urgency override for Eisenhower hybrid board (1 = urgent, 0 = less urgent).
-- NULL means auto: derive from due date / blocked status.
ALTER TABLE tasks ADD COLUMN urgency INTEGER NULL;
