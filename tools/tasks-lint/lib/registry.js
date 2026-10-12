// The task registry (docs/TASKS.md) is a Markdown table that is edited by hand
// and by scripts. One stray pipe moves every later cell of a row one column to
// the right: the status ends up in the evidence and the task reads as open or
// done when it is not (T-208). These checks read a row the way Markdown does.

export const STATUSES = ['Açıq', 'İcrada', 'Tamamlandı', 'Ləğv edildi', 'Təxirə salındı'];

const COLUMNS = ['ID', 'Tarix', 'Addım', 'Tapşırıq (qısa)', 'Status', 'Sübut (commit / test / fayl)'];

/** The cells of a table row: split on the pipes that are not escaped. */
export function cellsOf(line) {
  return line
    .replace(/^\s*\|/, '')
    .replace(/\|\s*$/, '')
    .split(/(?<!\\)\|/)
    .map((cell) => cell.trim());
}

const isRow = (line) => /^\s*\|\s*T-\d+\b/.test(line);

/**
 * Everything wrong with the registry, as `{ line, id, message }`.
 * An empty list means the table says what its author meant.
 */
export function lint(text) {
  const problems = [];
  const seen = new Map();
  const lines = text.split(/\r?\n/);

  const header = lines.findIndex((line) => /^\s*\|\s*ID\s*\|/.test(line));
  if (header < 0) {
    problems.push({ line: 1, id: '', message: 'the table header (| ID | Tarix | ...) is missing' });
  } else if (cellsOf(lines[header]).join('|') !== COLUMNS.join('|')) {
    problems.push({ line: header + 1, id: '', message: `the header must be: ${COLUMNS.join(' | ')}` });
  }

  for (const [index, line] of lines.entries()) {
    if (!isRow(line)) continue;

    const cells = cellsOf(line);
    const id = cells[0];
    const report = (message) => problems.push({ line: index + 1, id, message });

    if (!/^T-\d{3,}$/.test(id)) report(`"${id}" is not a task id (T-001)`);

    if (seen.has(id)) report(`the id is already used on line ${seen.get(id)}`);
    else seen.set(id, index + 1);

    if (cells.length !== COLUMNS.length) {
      report(`${cells.length} cells instead of ${COLUMNS.length} - a "|" inside a cell must be written "\\|"`);
      continue;
    }

    if (!/^\d{4}-\d{2}-\d{2}$/.test(cells[1])) report(`the date "${cells[1]}" is not YYYY-MM-DD`);
    if (!cells[2]) report('the step is empty');
    if (!cells[3]) report('the description is empty');

    const status = STATUSES.find((known) => cells[4] === known || cells[4].startsWith(`${known} `));
    if (!status) report(`the status "${cells[4].slice(0, 40)}" is not one of: ${STATUSES.join(', ')}`);
    if (status === 'Tamamlandı' && !cells[5]) report('a finished task has no evidence');
  }

  if (seen.size === 0) problems.push({ line: 1, id: '', message: 'the table has no tasks' });

  return problems;
}
