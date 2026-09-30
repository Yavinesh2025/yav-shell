export async function loadAll(names, load) {
  return Promise.all(names.map((name) => load(name)));
}
