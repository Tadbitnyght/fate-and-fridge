import { useState, type FormEvent } from 'react'
import { Link, useLocation } from 'react-router-dom'

import {
  RECIPE_SORT_LABELS,
  RECIPE_SORTS,
  RECIPE_STATUS_LABELS,
  RECIPE_STATUSES,
  type RecipeListItem,
  type RecipeListParams,
  type RecipeSort,
  type RecipeStatus,
} from '../../api/recipes'
import { useProteins, useTags } from '../reference-data/useReferenceData'
import { useRecipeAccess } from './access'
import { RecipeHistoryStats } from './RecipeHistoryStats'
import { historyCountLabel, historyDateLabel } from './recipeMetrics'
import { useRecipeList } from './useRecipes'

const PAGE_LIMIT = 24

type ViewMode = 'grid' | 'list'

/** Photo-or-fallback thumbnail shared by the grid and list views. */
function RecipeThumbnail({ recipe }: { recipe: RecipeListItem }) {
  const image = recipe.image
  const imageUrl = image?.url ?? null
  const [failedUrl, setFailedUrl] = useState<string | null>(null)

  // A replaced image gets a new URL; forget a previous load failure so the new
  // bytes are attempted rather than leaving the card stuck on the fallback.
  if (failedUrl !== null && failedUrl !== imageUrl) {
    setFailedUrl(null)
  }

  if (imageUrl === null || failedUrl === imageUrl) {
    return (
      <span className="recipe-thumb recipe-thumb--empty" aria-hidden="true">
        No photo
      </span>
    )
  }

  return (
    <img
      className="recipe-thumb"
      src={imageUrl}
      alt={image?.alt_text ?? ''}
      loading="lazy"
      onError={() => setFailedUrl(imageUrl)}
    />
  )
}

function RecipeBadges({ recipe }: { recipe: RecipeListItem }) {
  return (
    <ul className="recipe-badges">
      <li className="tag">{RECIPE_STATUS_LABELS[recipe.status]}</li>
      {recipe.is_favorite && <li className="tag tag--ok">Favorite</li>}
      {recipe.is_vegan && <li className="tag">Vegan</li>}
      {recipe.is_vegetarian && <li className="tag">Vegetarian</li>}
    </ul>
  )
}

function RecipeName({ recipe, canEdit }: { recipe: RecipeListItem; canEdit: boolean }) {
  if (canEdit) {
    return (
      <Link className="recipe-card__name-link" to={`/recipes/${recipe.id}/edit`}>
        {recipe.name}
      </Link>
    )
  }
  return <span className="recipe-card__name-text">{recipe.name}</span>
}

/**
 * Recipe browser (docs/PRODUCT_SPEC.md §27, issue #38). Photo card grid with a
 * compact list toggle; search/filters/sort are applied server-side through the
 * list API with `limit`/`offset` paging. Write-affording UI only appears for
 * actors with `recipes.edit`; the server stays authoritative.
 */
export function RecipesBrowser() {
  const { canEdit } = useRecipeAccess()
  const location = useLocation()

  // The M12-09 hard delete navigates here with the counts to report, because
  // the deleted recipe's editor no longer exists to show them.
  const notice =
    typeof (location.state as { notice?: unknown } | null)?.notice === 'string'
      ? (location.state as { notice: string }).notice
      : null

  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<'' | RecipeStatus>('')
  const [favorite, setFavorite] = useState(false)
  const [vegan, setVegan] = useState(false)
  const [vegetarian, setVegetarian] = useState(false)
  const [proteinId, setProteinId] = useState('')
  const [tagId, setTagId] = useState('')
  const [sort, setSort] = useState<RecipeSort>('recently_added')
  const [view, setView] = useState<ViewMode>('grid')
  const [offset, setOffset] = useState(0)

  const params: RecipeListParams = {
    search: search.length > 0 ? search : undefined,
    status: status === '' ? undefined : status,
    favorite: favorite ? true : undefined,
    vegan: vegan ? true : undefined,
    vegetarian: vegetarian ? true : undefined,
    protein_id: proteinId === '' ? undefined : Number(proteinId),
    tag_id: tagId === '' ? undefined : Number(tagId),
    sort,
    limit: PAGE_LIMIT,
    offset,
  }

  const recipesQuery = useRecipeList(params)
  const proteinsQuery = useProteins('', false)
  const tagsQuery = useTags('')

  const recipes = recipesQuery.data?.items ?? []
  const total = recipesQuery.data?.total ?? 0
  const proteins = proteinsQuery.data ?? []
  const tags = tagsQuery.data ?? []

  const hasFilters =
    search.length > 0 ||
    status !== '' ||
    favorite ||
    vegan ||
    vegetarian ||
    proteinId !== '' ||
    tagId !== ''

  function applyFilter(update: () => void): void {
    update()
    setOffset(0)
  }

  function handleSearch(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault()
    applyFilter(() => setSearch(searchInput.trim()))
  }

  function clearFilters(): void {
    setSearchInput('')
    applyFilter(() => {
      setSearch('')
      setStatus('')
      setFavorite(false)
      setVegan(false)
      setVegetarian(false)
      setProteinId('')
      setTagId('')
    })
  }

  const start = total === 0 ? 0 : offset + 1
  const end = Math.min(offset + recipes.length, total)

  return (
    <section className="page recipes" aria-labelledby="recipes-heading">
      <div className="recipes__header">
        <h2 id="recipes-heading" tabIndex={-1}>
          Recipes
        </h2>
        {canEdit && (
          <div className="recipes__header-actions">
            <Link className="recipes__import" to="/recipes/import">
              Import from URL
            </Link>
            <Link className="recipes__new button--primary" to="/recipes/new">
              Add recipe
            </Link>
          </div>
        )}
      </div>

      {notice !== null && (
        <p className="form__notice" role="status">
          {notice}
        </p>
      )}

      <form className="recipes__filters" onSubmit={handleSearch}>
        <div className="form__field recipes__search">
          <label htmlFor="recipe-search">Search recipes</label>
          <input
            id="recipe-search"
            type="search"
            value={searchInput}
            placeholder="Search by name…"
            onChange={(event) => setSearchInput(event.target.value)}
          />
        </div>

        <div className="form__field">
          <label htmlFor="recipe-status">Status</label>
          <select
            id="recipe-status"
            value={status}
            onChange={(event) =>
              applyFilter(() => setStatus(event.target.value as '' | RecipeStatus))
            }
          >
            <option value="">Any status</option>
            {RECIPE_STATUSES.map((value) => (
              <option key={value} value={value}>
                {RECIPE_STATUS_LABELS[value]}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="recipe-protein">Primary protein</label>
          <select
            id="recipe-protein"
            value={proteinId}
            onChange={(event) => applyFilter(() => setProteinId(event.target.value))}
          >
            <option value="">Any protein</option>
            {proteins.map((protein) => (
              <option key={protein.id} value={protein.id}>
                {protein.name}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="recipe-tag">Tag</label>
          <select
            id="recipe-tag"
            value={tagId}
            onChange={(event) => applyFilter(() => setTagId(event.target.value))}
          >
            <option value="">Any tag</option>
            {tags.map((tag) => (
              <option key={tag.id} value={tag.id}>
                {tag.name}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="recipe-sort">Sort</label>
          <select
            id="recipe-sort"
            value={sort}
            onChange={(event) => applyFilter(() => setSort(event.target.value as RecipeSort))}
          >
            {RECIPE_SORTS.map((value) => (
              <option key={value} value={value}>
                {RECIPE_SORT_LABELS[value]}
              </option>
            ))}
          </select>
        </div>

        <div className="recipes__flag-filters">
          <label className="recipes__checkbox">
            <input
              type="checkbox"
              checked={favorite}
              onChange={(event) => applyFilter(() => setFavorite(event.target.checked))}
            />
            Favorites only
          </label>
          <label className="recipes__checkbox">
            <input
              type="checkbox"
              checked={vegan}
              onChange={(event) => applyFilter(() => setVegan(event.target.checked))}
            />
            Vegan
          </label>
          <label className="recipes__checkbox">
            <input
              type="checkbox"
              checked={vegetarian}
              onChange={(event) => applyFilter(() => setVegetarian(event.target.checked))}
            />
            Vegetarian
          </label>
        </div>

        <div className="recipes__filter-actions">
          <button type="submit">Search</button>
          {hasFilters && (
            <button type="button" onClick={clearFilters}>
              Clear filters
            </button>
          )}
        </div>
      </form>

      <div className="recipes__view-bar">
        <div className="recipes__view-toggle" role="group" aria-label="Recipe view">
          <button type="button" aria-pressed={view === 'grid'} onClick={() => setView('grid')}>
            Grid
          </button>
          <button type="button" aria-pressed={view === 'list'} onClick={() => setView('list')}>
            List
          </button>
        </div>
        <p className="recipes__count" role="status">
          {total === 0 ? 'No recipes' : `${total} recipe${total === 1 ? '' : 's'}`}
        </p>
      </div>

      {recipesQuery.isPending ? (
        <p className="page-note" role="status">
          Loading recipes…
        </p>
      ) : recipesQuery.isError ? (
        <div className="recipes__error" role="alert">
          <p>Cannot load recipes. Check the connection and try again.</p>
          <button type="button" onClick={() => void recipesQuery.refetch()}>
            Try again
          </button>
        </div>
      ) : recipes.length === 0 ? (
        <div className="recipes__empty">
          {hasFilters ? (
            <>
              <p className="page-note">No recipes match these filters.</p>
              <button type="button" onClick={clearFilters}>
                Clear filters
              </button>
            </>
          ) : (
            <>
              <p className="page-note">
                No recipes yet. Add the first one to start planning dinners.
              </p>
              {canEdit && (
                <Link className="button--primary" to="/recipes/new">
                  Add recipe
                </Link>
              )}
            </>
          )}
        </div>
      ) : view === 'grid' ? (
        <ul className="recipe-grid">
          {recipes.map((recipe) => (
            <li key={recipe.id} className="recipe-card">
              <RecipeThumbnail recipe={recipe} />
              <div className="recipe-card__body">
                <h3 className="recipe-card__name">
                  <RecipeName recipe={recipe} canEdit={canEdit} />
                </h3>
                <RecipeBadges recipe={recipe} />
                <RecipeHistoryStats metrics={recipe} />
              </div>
            </li>
          ))}
        </ul>
      ) : (
        <div className="table-wrap">
          <table className="data-table" aria-label="Recipes">
            <thead>
              <tr>
                <th scope="col">Photo</th>
                <th scope="col">Name</th>
                <th scope="col">Status</th>
                <th scope="col">Last eaten</th>
                <th scope="col">Next planned</th>
                <th scope="col">Times eaten</th>
              </tr>
            </thead>
            <tbody>
              {recipes.map((recipe) => (
                <tr key={recipe.id}>
                  <td>
                    <RecipeThumbnail recipe={recipe} />
                  </td>
                  <th scope="row">
                    <RecipeName recipe={recipe} canEdit={canEdit} />
                    {recipe.is_favorite && <span className="tag tag--ok">Favorite</span>}
                  </th>
                  <td>{RECIPE_STATUS_LABELS[recipe.status]}</td>
                  <td>{historyDateLabel(recipe.last_eaten)}</td>
                  <td>{historyDateLabel(recipe.next_planned)}</td>
                  <td>{historyCountLabel(recipe.times_eaten)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {total > 0 && (
        <nav className="recipes__paging" aria-label="Recipe pages">
          <button
            type="button"
            disabled={offset === 0}
            onClick={() => setOffset(Math.max(0, offset - PAGE_LIMIT))}
          >
            Previous
          </button>
          <p>
            Showing {start}–{end} of {total}
          </p>
          <button
            type="button"
            disabled={end >= total}
            onClick={() => setOffset(offset + PAGE_LIMIT)}
          >
            Next
          </button>
        </nav>
      )}
    </section>
  )
}
