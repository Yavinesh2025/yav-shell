export class ValidationError extends Error {
  constructor(field, message) {
    super(`${field} ${message}`);
    this.name = 'ValidationError';
    this.field = field;
  }
}

export function createUser({ name, email }) {
  const cleanName = typeof name === 'string' ? name.trim() : '';
  if (cleanName.length === 0) {
    throw new ValidationError('name', 'is missing');
  }

  const cleanEmail = typeof email === 'string' ? email.trim() : '';
  if (!cleanEmail.includes('@')) {
    throw new ValidationError('email', 'is not an address');
  }

  return { name: cleanName, email: cleanEmail };
}
