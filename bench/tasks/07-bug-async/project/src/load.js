export async function loadAll(names, load) {
  const results = [];
  names.forEach(async (name) => {
    results.push(await load(name));
  });
  return results;
}
