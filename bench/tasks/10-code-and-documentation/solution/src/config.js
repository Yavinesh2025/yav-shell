export function port(environment = process.env) {
  return Number(environment.PORT ?? 8080);
}
